import { useCallback, useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { AiText, api, ApiError, ONBOARDING_STAGES, STAGES, Submission } from '../api';
import { useAuth } from '../auth';
import { AiBadge, Avatar, copyText, Empty, Modal, Spinner, useConfirm, useErrorHandler, useToast } from '../ui';

export default function Pipeline() {
  const { scope, can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [subs, setSubs] = useState<Submission[] | null>(null);
  const [drag, setDrag] = useState<number | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const [prep, setPrep] = useState<AiText | 'loading' | null>(null);
  const [params, setParams] = useSearchParams();
  const ownerId = Number(params.get('owner')) || null;   // recruiter filter, also set from the dashboard table

  const load = useCallback(() => { api<Submission[]>('/submissions', { query: { scope } }).then(setSubs).catch(e => { setSubs([]); onError(e); }); }, [scope, onError]);
  useEffect(load, [load]);

  const move = async (s: Submission, stage: string) => {
    if (stage === s.stage) return;
    if (stage === '__withdraw') {
      if (!await confirm({ title: 'Withdraw submission?', message: `${s.consultantName} will be withdrawn from "${s.title}". Tell the vendor as well.`, okLabel: 'Withdraw', danger: true })) return;
      try { await api(`/submissions/${s.id}`, { method: 'DELETE' }); notify('success', 'Submission withdrawn'); load(); } catch (e) { onError(e); }
      return;
    }
    // optimistic update, rolled back on error
    setSubs(xs => xs?.map(x => (x.id === s.id ? { ...x, stage } : x)) ?? null);
    try {
      await api(`/submissions/${s.id}/stage`, { method: 'POST', body: { toStage: stage, rowVersion: s.rowVersion } });
      notify('success', stage === 'Billing' ? `🎉 ${s.consultantName} is placed` : 'Stage updated', `${s.consultantName} → ${stage}`);
    } catch (e) {
      onError(e);
      if (e instanceof ApiError && e.code === 'ONBOARDING_INCOMPLETE') notify('info', 'Finish onboarding first', 'Open Onboarding & Billing to complete the checklist.');
    } finally { load(); }
  };

  const interviewPrep = async (id: number) => {
    setPrep('loading');
    try { setPrep(await api<AiText>(`/ai/interview-prep/${id}`, { method: 'POST' })); } catch (e) { setPrep(null); onError(e); }
  };

  if (!subs) return <Empty><Spinner dark /> Loading pipeline…</Empty>;
  const owners = [...new Map(subs.map(s => [s.ownerId, s.ownerName])).entries()].sort((a, b) => a[1].localeCompare(b[1]));
  const shown = ownerId ? subs.filter(s => s.ownerId === ownerId) : subs;
  const setOwner = (id: string) => setParams(p => { if (id) p.set('owner', id); else p.delete('owner'); return p; }, { replace: true });
  return (
    <>
      <div className="ph"><div><h1>Submissions Pipeline</h1><p>Drag cards between stages, or use the menu on each card. {!can('onboard.edit') && <><b>MSA / PO</b> and <b>Billing</b> need a Team Lead.</>}</p></div>
        <div className="ph-act">
          {(owners.length > 1 || ownerId) && <select className="inp" style={{ width: 'auto' }} aria-label="Filter by recruiter" value={ownerId ?? ''} onChange={e => setOwner(e.target.value)}>
            <option value="">All recruiters ({subs.length})</option>
            {owners.map(([id, name]) => <option key={id} value={id}>{name} ({subs.filter(s => s.ownerId === id).length})</option>)}
            {ownerId && !owners.some(([id]) => id === ownerId) && <option value={ownerId}>Selected recruiter (0)</option>}
          </select>}
          {can('match.view') && <Link className="btn btn-dark" to="/match">+ New submission</Link>}
        </div></div>
      <div className="kanban">
        {STAGES.map(st => {
          const items = shown.filter(s => s.stage === st);
          const locked = ONBOARDING_STAGES.includes(st) && !can('onboard.edit');
          return (
            <div key={st} className={`col${over === st ? ' over' : ''}`} onDragOver={e => { e.preventDefault(); setOver(st); }} onDragLeave={() => setOver(null)}
              onDrop={e => { e.preventDefault(); setOver(null); const s = subs.find(x => x.id === drag); setDrag(null); if (s) move(s, st); }}>
              <div className="col-h">{st}{locked && ' 🔒'}<span>{items.length}</span></div>
              {items.map(s => (
                <div key={s.id} className={`kc${s.daysInStage >= 3 && st !== 'Billing' ? ' stale' : ''}`} draggable onDragStart={() => setDrag(s.id)}>
                  <div className="flex" style={{ gap: 8, flexWrap: 'nowrap' }}><Avatar name={s.consultantName} size={28} /><div style={{ minWidth: 0 }}><b>{s.consultantName}</b><small>{s.title}</small></div></div>
                  <small style={{ marginTop: 8 }}>{s.client} · {s.vendor}</small>
                  <small>{s.type} · <span className="mono">${s.submitRate}/hr</span> {s.rtr && <span className="tag g" style={{ fontSize: 10.5 }}>RTR</span>}
                    {s.daysInStage >= 3 && st !== 'Billing' && <span className="tag y" style={{ fontSize: 10.5 }}>{s.daysInStage}d in stage</span>}</small>
                  {s.notes && <small style={{ marginTop: 4, color: 'var(--ink-2)' }}>“{s.notes}”</small>}
                  {(st === 'Vendor Screen' || st === 'Client Interview') && <button className="btn btn-sm btn-outline" style={{ width: '100%', justifyContent: 'center', marginTop: 8 }} onClick={() => interviewPrep(s.id)}>✦ AI interview prep</button>}
                  <div className="kf"><span title={`Owner: ${s.ownerName}`}><Avatar name={s.ownerName} size={22} /></span>
                    {can('sub.move') && <select aria-label="Move stage" value={s.stage} onChange={e => move(s, e.target.value)}>
                      {STAGES.map(x => <option key={x}>{x}</option>)}{can('sub.withdraw') && <option value="__withdraw">Withdraw…</option>}</select>}</div>
                </div>))}
            </div>);
        })}
      </div>
      {prep && <Modal title="AI interview prep" wide onClose={() => setPrep(null)}
        footer={<><button className="btn btn-outline" onClick={() => setPrep(null)}>Close</button>{prep !== 'loading' && <button className="btn btn-primary" onClick={() => copyText(prep.text, notify)}>Copy for consultant</button>}</>}>
        {prep === 'loading' ? <Empty><Spinner dark /> Preparing questions…</Empty> : <><AiBadge source={prep.source} /><div className="jd mt">{prep.text}</div></>}
      </Modal>}
    </>
  );
}
