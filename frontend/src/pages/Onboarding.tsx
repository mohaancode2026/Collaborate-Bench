import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, Submission } from '../api';
import { useAuth } from '../auth';
import { Avatar, Empty, Spinner, money, statusClass, useErrorHandler, useToast } from '../ui';

interface Item { code: string; label: string; done: boolean; doneBy?: string; doneUtc?: string; }
const REQUIRED = ['MSA', 'PO', 'RTR_OFFER', 'I9', 'BGV', 'DRUG'];

export default function Onboarding() {
  const { scope, can } = useAuth();
  const onError = useErrorHandler();
  const [subs, setSubs] = useState<Submission[] | null>(null);
  const load = useCallback(() => {
    api<Submission[]>('/submissions', { query: { scope } }).then(s => setSubs(s.filter(x => ['Offer', 'MSA / PO', 'Billing'].includes(x.stage)))).catch(e => { setSubs([]); onError(e); });
  }, [scope, onError]);
  useEffect(load, [load]);
  if (!subs) return <Empty><Spinner dark /> Loading…</Empty>;
  return (
    <>
      <div className="ph"><div><h1>Onboarding &amp; Billing</h1><p>Compliance checklist and billing from offer onward. {!can('onboard.edit') && <span className="tag s">🔒 Read-only for your role</span>}</p></div></div>
      {subs.length ? <div className="grid g2">{subs.map(s => <Card key={s.id} s={s} onChanged={load} />)}</div>
        : <div className="card"><Empty><b>No consultants in offer or onboarding in your view.</b><div>Move a submission to Offer on the <Link to="/pipeline" style={{ color: 'var(--primary)' }}>pipeline</Link> and it shows up here.</div></Empty></div>}
    </>
  );
}

function Card({ s, onChanged }: { s: Submission; onChanged: () => void }) {
  const { can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const [items, setItems] = useState<Item[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const load = useCallback(() => { api<Item[]>(`/submissions/${s.id}/onboarding`).then(setItems).catch(onError); }, [s.id, onError]);
  useEffect(load, [load]);
  const edit = can('onboard.edit');
  const done = items?.filter(i => i.done).length ?? 0;
  const ready = !!items && REQUIRED.every(c => items.find(i => i.code === c)?.done);

  const toggle = async (i: Item) => {
    setBusy(i.code);
    try { await api(`/submissions/${s.id}/onboarding/${i.code}`, { method: 'PUT', body: { done: !i.done } }); load(); }
    catch (e) { onError(e); } finally { setBusy(null); }
  };
  const bill = async () => {
    try { await api(`/submissions/${s.id}/stage`, { method: 'POST', body: { toStage: 'Billing', rowVersion: s.rowVersion } }); notify('success', `🎉 ${s.consultantName} is placed and billing`); onChanged(); }
    catch (e) { onError(e); onChanged(); }
  };

  return (
    <div className="card">
      <div className="card-h"><div className="who"><Avatar name={s.consultantName} /><div><b>{s.consultantName}</b><small>{s.title} · {s.client} · owner {s.ownerName}</small></div></div><span className={`tag ${statusClass(s.stage)}`}>{s.stage}</span></div>
      <div className="card-b">
        {!items ? <Spinner dark /> : <>
          <div className="flex between" style={{ fontSize: 13, marginBottom: 6 }}><span>Compliance progress</span><b>{done}/{items.length}</b></div>
          <div className="progress"><i style={{ width: `${done / items.length * 100}%` }} /></div>
          <div className="mt">{items.map(i => (
            <label key={i.code} className={`check${i.done ? ' done' : ''}`}>
              <input type="checkbox" checked={i.done} disabled={!edit || busy === i.code || s.stage === 'Offer' && !edit} onChange={() => toggle(i)} />
              <span>{i.label}{i.done && i.doneBy && <small className="muted"> · {i.doneBy}</small>}</span></label>))}</div>
        </>}
        <div className="grid g3 mt" style={{ gap: 10 }}>
          <div className="card" style={{ padding: 12, background: '#f8fafc' }}><small className="muted">Bill to vendor</small><b className="mono" style={{ display: 'block' }}>${s.submitRate}/hr</b></div>
          <div className="card" style={{ padding: 12, background: '#f8fafc' }}><small className="muted">Weekly margin</small><b style={{ display: 'block' }}>{s.marginPerHour != null ? money(s.marginPerHour * 40) : '🔒 Hidden'}</b></div>
          <div className="card" style={{ padding: 12, background: '#f8fafc' }}><small className="muted">Match score</small><b style={{ display: 'block' }}>{s.matchScore ?? '—'}%</b></div>
        </div>
        {edit && s.stage !== 'Billing' && <button className="btn btn-primary mt" style={{ width: '100%', justifyContent: 'center' }} disabled={!ready} title={ready ? '' : 'Complete MSA, PO, RTR/offer, I-9, background check and drug screen first'} onClick={bill}>Start billing</button>}
      </div>
    </div>
  );
}
