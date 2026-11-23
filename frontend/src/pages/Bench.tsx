import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, ApiError, Consultant } from '../api';
import { useAuth } from '../auth';
import { ConsultantDraft, ConsultantForm, emptyDraft, toDraft, toInput } from '../components';
import { Avatar, copyText, Empty, FieldErrors, Modal, Spinner, statusClass, useConfirm, useErrorHandler, useToast, visaClass } from '../ui';

export default function Bench() {
  const { scope, can } = useAuth();
  const [params, setParams] = useSearchParams();
  const nav = useNavigate();
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [list, setList] = useState<Consultant[] | null>(null);
  const [tab, setTab] = useState('All');
  const [q, setQ] = useState(params.get('q') ?? '');
  const [edit, setEdit] = useState<{ id?: number; draft: ConsultantDraft } | null>(null);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const [hot, setHot] = useState<string | null>(null);

  const load = useCallback(() => { api<Consultant[]>('/consultants', { query: { scope } }).then(setList).catch(e => { setList([]); onError(e); }); }, [scope, onError]);
  useEffect(load, [load]);
  useEffect(() => { setQ(params.get('q') ?? ''); }, [params]);

  const shown = (list ?? []).filter(c => (tab === 'All' || c.status === tab) &&
    (!q || `${c.name} ${c.title} ${c.location} ${c.visa} ${c.ownerName} ${c.skills.join(' ')}`.toLowerCase().includes(q.toLowerCase())));

  const save = async () => {
    if (!edit) return;
    setBusy(true); setErrors({});
    try {
      const body = toInput(edit.draft);
      if (edit.id) await api(`/consultants/${edit.id}`, { method: 'PUT', body });
      else await api('/consultants', { method: 'POST', body });
      notify('success', 'Consultant saved', `${edit.draft.name} is on the bench.`);
      setEdit(null); load();
    } catch (e) {
      onError(e, setErrors);
      if (e instanceof ApiError && e.code === 'CONFLICT') load();
    } finally { setBusy(false); }
  };

  const remove = async (id: number, name: string, force = false): Promise<void> => {
    if (!force && !await confirm({ title: 'Remove consultant?', message: `${name} will be removed from the bench.`, okLabel: 'Remove', danger: true })) return;
    try {
      await api(`/consultants/${id}`, { method: 'DELETE', query: { force } });
      notify('success', 'Consultant removed', name); setEdit(null); load();
    } catch (e) {
      if (e instanceof ApiError && e.code === 'HAS_ACTIVE_SUBMISSIONS' && await confirm({ title: e.title, message: e.detail, okLabel: 'Withdraw & remove', danger: true }))
        return remove(id, name, true);
      onError(e);
    }
  };

  const hotlist = async () => {
    try { setHot((await api<{ text: string }>('/consultants/hotlist', { query: { scope } })).text); } catch (e) { onError(e); }
  };

  return (
    <>
      <div className="ph"><div><h1>Bench &amp; Hotlist</h1><p>Consultants in your current view.</p></div>
        <div className="ph-act">
          <button className="btn btn-outline" onClick={hotlist}>Generate hotlist</button>
          {can('parser.use') && <button className="btn btn-outline" onClick={() => nav('/parser')}>✦ AI parse resume</button>}
          {can('bench.create') && <button className="btn btn-dark" onClick={() => { setErrors({}); setEdit({ draft: emptyDraft() }); }}>+ Add consultant</button>}
        </div></div>
      <div className="card">
        <div className="card-h">
          <div className="tabs">{['All', 'Bench', 'Marketing', 'Placed'].map(t => <button key={t} className={t === tab ? 'on' : ''} onClick={() => setTab(t)}>{t} ({t === 'All' ? list?.length ?? 0 : list?.filter(c => c.status === t).length ?? 0})</button>)}</div>
          <input className="inp" placeholder="Filter by skill, visa, recruiter…" style={{ maxWidth: 280 }} value={q} onChange={e => { setQ(e.target.value); setParams(e.target.value ? { q: e.target.value } : {}, { replace: true }); }} />
        </div>
        {list === null ? <Empty><Spinner dark /> Loading…</Empty> : (
          <div className="tw"><table><thead><tr><th>Consultant</th><th>Skills</th><th>Visa</th><th>Location</th><th>Exp</th><th>Rate</th><th>Owner</th><th>Status</th><th></th></tr></thead><tbody>
            {shown.map(c => (
              <tr key={c.id}>
                <td><div className="who"><Avatar name={c.name} /><div><b>{c.name}</b><small>{c.title} · {c.daysOnBench}d on bench</small></div></div></td>
                <td style={{ minWidth: 220 }}>{c.skills.slice(0, 4).map(s => <span key={s} className="tag">{s}</span>)}{c.skills.length > 4 && <span className="tag s">+{c.skills.length - 4}</span>}</td>
                <td><span className={`tag ${visaClass(c.visa)}`}>{c.visa || '—'}</span></td>
                <td style={{ whiteSpace: 'nowrap' }}>{c.location}{c.relocate && <span className="tag s"> Reloc</span>}</td>
                <td>{c.exp}y</td><td className="mono">${c.rate}/hr</td>
                <td style={{ whiteSpace: 'nowrap' }}>{c.ownerName}<br /><small className="muted">{c.ownerTeam}</small></td>
                <td><span className={`tag ${statusClass(c.status)}`}>{c.status}</span></td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button className="btn btn-sm btn-outline" onClick={() => nav(`/match?consultant=${c.id}`)}>Match</button>{' '}
                  {can('bench.edit') && <button className="btn btn-sm btn-outline" onClick={() => { setErrors({}); setEdit({ id: c.id, draft: { ...toDraft(c), id: c.id } }); }}>Edit</button>}
                </td>
              </tr>))}
          </tbody></table>{shown.length === 0 && <Empty>No consultants match this filter in your view.</Empty>}</div>)}
      </div>

      {edit && (
        <Modal title={edit.id ? 'Edit consultant' : 'Add consultant'} onClose={() => setEdit(null)}
          footer={<>{edit.id && can('bench.delete') && <button className="btn btn-outline" style={{ marginRight: 'auto', color: 'var(--bad)' }} onClick={() => remove(edit.id!, edit.draft.name)}>Remove</button>}
            <button className="btn btn-outline" onClick={() => setEdit(null)}>Cancel</button>
            <button className="btn btn-primary" onClick={save} disabled={busy}>{busy ? <Spinner /> : 'Save'}</button></>}>
          <ConsultantForm draft={edit.draft} set={d => setEdit({ ...edit, draft: d })} errors={errors} />
        </Modal>)}
      {hot && (
        <Modal title="Hotlist email" onClose={() => setHot(null)} wide
          footer={<><button className="btn btn-outline" onClick={() => setHot(null)}>Close</button><button className="btn btn-primary" onClick={() => copyText(hot, notify)}>Copy hotlist</button></>}>
          <div className="jd mono" style={{ fontSize: 12.5 }}>{hot}</div>
        </Modal>)}
    </>
  );
}
