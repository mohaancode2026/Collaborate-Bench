import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { AiText, api, ApiError, CHANNELS, Requirement } from '../api';
import { useAuth } from '../auth';
import { AiBadge, copyText, Empty, Field, FieldErrors, Modal, Spinner, statusClass, useConfirm, useErrorHandler, useToast } from '../ui';

interface ReqDraft { title: string; client: string; vendor: string; tier: string; location: string; mode: string; type: string; rate: number | ''; skills: string; visas: string[]; }
const blank: ReqDraft = { title: '', client: '', vendor: '', tier: 'Direct', location: '', mode: 'Hybrid', type: 'C2C', rate: '', skills: '', visas: ['Any'] };
const toBody = (d: ReqDraft) => ({ ...d, rate: Number(d.rate) || 0, skills: d.skills.split(',').map(s => s.trim()).filter(Boolean), visas: d.visas.length ? d.visas : ['Any'] });

export default function Jobs() {
  const { can } = useAuth();
  const [params, setParams] = useSearchParams();
  const tab = can('jobs.create') ? params.get('tab') ?? 'list' : 'list';
  return (
    <>
      <div className="ph"><div><h1>Jobs &amp; Posting Agent</h1><p>Requirements are shared across all teams. Team Leads and above can close or reopen them.</p></div>
        <div className="tabs">{[['list', 'Requirements'], ...(can('jobs.create') ? [['post', 'Posting agent'], ['source', 'Sourcing agent']] : [])].map(([k, t]) =>
          <button key={k} className={tab === k ? 'on' : ''} onClick={() => setParams({ tab: k })}>{t}</button>)}</div></div>
      {tab === 'list' ? <List /> : tab === 'post' ? <Post /> : <Source />}
    </>
  );
}

function List() {
  const { can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [list, setList] = useState<Requirement[] | null>(null);
  const [view, setView] = useState<Requirement | null>(null);
  const load = useCallback(() => { api<Requirement[]>('/requirements').then(setList).catch(e => { setList([]); onError(e); }); }, [onError]);
  useEffect(load, [load]);

  const toggle = async (r: Requirement, force = false): Promise<void> => {
    const status = r.status === 'Open' ? 'Closed' : 'Open';
    try { await api(`/requirements/${r.id}/status`, { method: 'PATCH', body: { status }, query: { force } }); notify('success', `Requirement ${status === 'Open' ? 'reopened' : 'closed'}`, r.title); load(); }
    catch (e) {
      if (e instanceof ApiError && e.code === 'HAS_ACTIVE_SUBMISSIONS' && await confirm({ title: e.title, message: e.detail, okLabel: 'Close requirement' })) return toggle(r, true);
      onError(e);
    }
  };

  if (!list) return <Empty><Spinner dark /> Loading…</Empty>;
  return (
    <div className="card"><div className="tw"><table><thead><tr><th>Requirement</th><th>Client / Vendor</th><th>Type</th><th>Bill rate</th><th>Skills</th><th>Visa</th><th>Posted by</th><th>Status</th><th></th></tr></thead><tbody>
      {list.map(r => (
        <tr key={r.id}>
          <td><b>{r.title}</b><br /><small className="muted">{r.location} · {r.mode} · {r.daysOld === 0 ? 'today' : `${r.daysOld}d ago`}</small></td>
          <td style={{ whiteSpace: 'nowrap' }}>{r.client}<br /><small className="muted">{r.vendor} · {r.tier}</small></td>
          <td><span className="tag v">{r.type}</span></td><td className="mono">${r.rate}/hr</td>
          <td style={{ minWidth: 200 }}>{r.skills.slice(0, 4).map(s => <span key={s} className="tag">{s}</span>)}</td>
          <td style={{ minWidth: 110 }}>{r.visas.map(v => <span key={v} className="tag s">{v}</span>)}</td>
          <td style={{ whiteSpace: 'nowrap' }}>{r.postedByName}</td><td><span className={`tag ${statusClass(r.status)}`}>{r.status}</span></td>
          <td style={{ whiteSpace: 'nowrap' }}><button className="btn btn-sm btn-outline" onClick={() => setView(r)}>View</button>{' '}
            {can('jobs.close') && <button className="btn btn-sm btn-outline" onClick={() => toggle(r)}>{r.status === 'Open' ? 'Close' : 'Reopen'}</button>}</td>
        </tr>))}
    </tbody></table>{list.length === 0 && <Empty>No requirements yet.</Empty>}</div>
      {view && <Modal title={view.title} onClose={() => setView(null)} wide footer={<><button className="btn btn-outline" onClick={() => setView(null)}>Close</button><button className="btn btn-primary" onClick={() => copyText(view.description ?? '', notify)}>Copy</button></>}>
        <div className="flex" style={{ marginBottom: 10 }}><span className="tag v">{view.type}</span><span className="tag">${view.rate}/hr</span><span className="tag s">{view.client} · {view.vendor}</span></div>
        <div className="jd">{view.description || 'No description was saved for this requirement.'}</div></Modal>}
    </div>
  );
}

function ReqFields({ d, set, errors }: { d: ReqDraft; set: (d: ReqDraft) => void; errors: FieldErrors }) {
  const f = <K extends keyof ReqDraft>(k: K) => (v: ReqDraft[K]) => set({ ...d, [k]: v });
  return (
    <>
      <div className="row2"><Field label="Job title *" error={errors.title}><input className="inp" value={d.title} onChange={e => f('title')(e.target.value)} placeholder="e.g. Senior React Developer" /></Field>
        <Field label="End client *" error={errors.client}><input className="inp" value={d.client} onChange={e => f('client')(e.target.value)} /></Field></div>
      <div className="row3"><Field label="Vendor" error={errors.vendor}><input className="inp" value={d.vendor} onChange={e => f('vendor')(e.target.value)} placeholder="Direct / vendor name" /></Field>
        <Field label="Tier" error={errors.tier}><select className="inp" value={d.tier} onChange={e => f('tier')(e.target.value)}>{['Direct', 'Prime vendor', 'Implementation partner', 'Tier-1', 'Tier-2'].map(x => <option key={x}>{x}</option>)}</select></Field>
        <Field label="Contract" error={errors.type}><select className="inp" value={d.type} onChange={e => f('type')(e.target.value)}>{['C2C', 'W2', '1099'].map(x => <option key={x}>{x}</option>)}</select></Field></div>
      <div className="row3"><Field label="Location" error={errors.location}><input className="inp" value={d.location} onChange={e => f('location')(e.target.value)} placeholder="Boston, MA" /></Field>
        <Field label="Work mode" error={errors.mode}><select className="inp" value={d.mode} onChange={e => f('mode')(e.target.value)}>{['Hybrid', 'Remote', 'Onsite'].map(x => <option key={x}>{x}</option>)}</select></Field>
        <Field label="Bill rate ($/hr) *" error={errors.rate}><input className="inp" type="number" value={d.rate} onChange={e => f('rate')(e.target.value === '' ? '' : Number(e.target.value))} /></Field></div>
      <Field label="Required skills * (comma separated)" error={errors.skills}><input className="inp" value={d.skills} onChange={e => f('skills')(e.target.value)} placeholder="React, TypeScript, Node.js, AWS" /></Field>
      <Field label="Accepted visas"><div className="chips">{['Any', 'USC', 'GC', 'GC-EAD', 'H1B', 'H4-EAD', 'OPT', 'TN'].map(v => (
        <label key={v} className={`chip${d.visas.includes(v) ? ' on' : ''}`}><input type="checkbox" checked={d.visas.includes(v)} onChange={e => f('visas')(e.target.checked ? [...d.visas, v] : d.visas.filter(x => x !== v))} />{v}</label>))}</div></Field>
    </>
  );
}

function Post() {
  const notify = useToast();
  const onError = useErrorHandler();
  const confirm = useConfirm();
  const nav = useNavigate();
  const [d, setD] = useState<ReqDraft>(blank);
  const [jd, setJd] = useState('');
  const [src, setSrc] = useState<string>();
  const [channels, setChannels] = useState(['Dice', 'LinkedIn Jobs', 'Vendor Email']);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState<'' | 'jd' | 'post'>('');

  const gen = async () => {
    setBusy('jd'); setErrors({});
    try { const r = await api<AiText>('/ai/generate-jd', { method: 'POST', body: toBody(d) }); setJd(r.text); setSrc(r.source); if (r.notice) notify('info', 'Built-in engine used', r.notice); }
    catch (e) { onError(e, setErrors); } finally { setBusy(''); }
  };
  const post = async (force = false): Promise<void> => {
    setBusy('post'); setErrors({});
    try {
      const r = await api<Requirement>('/requirements', { method: 'POST', query: { force }, body: { ...toBody(d), description: jd, source: 'PostingAgent', channels } });
      notify('success', 'Requirement posted', `Queued for ${channels.length} channel${channels.length > 1 ? 's' : ''}.`);
      nav(`/match?requirement=${r.id}`);
    } catch (e) {
      setBusy('');
      if (e instanceof ApiError && e.code === 'DUPLICATE_REQUIREMENT' && await confirm({ title: e.title, message: e.detail, okLabel: 'Post anyway' })) return post(true);
      onError(e, setErrors);
    } finally { setBusy(''); }
  };
  return (
    <div className="grid g2">
      <div className="card"><div className="card-h"><h3>Requirement details</h3></div><div className="card-b">
        <ReqFields d={d} set={setD} errors={errors} />
        <button className="btn btn-dark" style={{ width: '100%', justifyContent: 'center' }} onClick={gen} disabled={!!busy}>{busy === 'jd' ? <><Spinner /> Writing JD…</> : '✦ Generate JD with agent'}</button>
      </div></div>
      <div className="card"><div className="card-h"><h3>Job description &amp; distribution</h3><AiBadge source={src} /></div><div className="card-b">
        <textarea className="inp" style={{ minHeight: 300, fontSize: 13.5 }} value={jd} onChange={e => setJd(e.target.value)} placeholder="The generated JD appears here. You can edit it before posting." />
        <Field label="Distribute to" error={errors.channels} className="mt"><div className="chips">{CHANNELS.map(c => (
          <label key={c} className={`chip${channels.includes(c) ? ' on' : ''}`}><input type="checkbox" checked={channels.includes(c)} onChange={e => setChannels(e.target.checked ? [...channels, c] : channels.filter(x => x !== c))} />{c}</label>))}</div></Field>
        <button className="btn btn-primary" style={{ width: '100%', justifyContent: 'center' }} onClick={() => post()} disabled={!!busy}>{busy === 'post' ? <Spinner /> : 'Publish & distribute'}</button>
      </div></div>
    </div>
  );
}

function Source() {
  const notify = useToast();
  const onError = useErrorHandler();
  const nav = useNavigate();
  const [text, setText] = useState('');
  const [d, setD] = useState<ReqDraft | null>(null);
  const [src, setSrc] = useState<string>();
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const extract = async () => {
    setBusy(true); setErrors({});
    try {
      const r = await api<{ requirement: { title?: string; client?: string; vendor?: string; tier?: string; location?: string; mode?: string; type?: string; rate: number; skills?: string[]; visas?: string[] }; source: string; notice?: string }>('/ai/extract-requirement', { method: 'POST', body: { text } });
      const q = r.requirement;
      setD({ title: q.title ?? '', client: q.client ?? '', vendor: q.vendor ?? '', tier: q.tier ?? 'Tier-1', location: q.location ?? '', mode: q.mode ?? 'Hybrid', type: q.type ?? 'C2C', rate: q.rate || '', skills: (q.skills ?? []).join(', '), visas: q.visas ?? ['Any'] });
      setSrc(r.source);
      if (r.notice) notify('info', 'Built-in engine used', r.notice);
      if (!q.title || !(q.skills?.length)) notify('warning', 'Low-confidence extraction', "Couldn't find a clear title or skills. Check the fields before saving.");
    } catch (e) { onError(e, setErrors); } finally { setBusy(false); }
  };
  const save = async () => {
    if (!d) return;
    setBusy(true); setErrors({});
    try { const r = await api<Requirement>('/requirements', { method: 'POST', body: { ...toBody(d), description: text, source: 'SourcingAgent' } }); notify('success', 'Requirement added', r.title); nav(`/match?requirement=${r.id}`); }
    catch (e) { onError(e, setErrors); } finally { setBusy(false); }
  };
  return (
    <div className="grid g2">
      <div className="card"><div className="card-h"><h3>Paste a vendor requirement email</h3></div><div className="card-b">
        <Field error={errors.text}><textarea className="inp" style={{ minHeight: 300 }} value={text} onChange={e => setText(e.target.value)} placeholder="Paste the requirement email from a vendor, prime or implementation partner" /></Field>
        <button className="btn btn-dark" style={{ width: '100%', justifyContent: 'center' }} onClick={extract} disabled={busy}>{busy && !d ? <Spinner /> : '✦ Extract requirement'}</button>
      </div></div>
      <div className="card"><div className="card-h"><h3>Structured requirement</h3><AiBadge source={src} /></div><div className="card-b">
        {!d ? <Empty>The sourcing agent turns unstructured emails into requirements you can match.</Empty> : <>
          <ReqFields d={d} set={setD} errors={errors} />
          <button className="btn btn-primary" style={{ width: '100%', justifyContent: 'center' }} onClick={save} disabled={busy}>+ Add requirement &amp; find matches</button></>}
      </div></div>
    </div>
  );
}
