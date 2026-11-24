import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { api, ApiError, Consultant } from '../api';
import { useAuth } from '../auth';
import { roleLabel } from '../roles';
import { ConsultantEditor } from '../consultantEditor';
import { AssignDialog } from '../assign';
import { HotlistDialog } from '../hotlist';
import { Pager } from '../pager';
import { Avatar, Empty, Spinner, statusClass, useConfirm, useErrorHandler, useToast, visaClass } from '../ui';

export default function Bench() {
  const { scope, can, user } = useAuth();
  const [params, setParams] = useSearchParams();
  const nav = useNavigate();
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [list, setList] = useState<Consultant[] | null>(null);
  const [tab, setTab] = useState('All');
  const [q, setQ] = useState(params.get('q') ?? '');
  const [adding, setAdding] = useState(false);
  const [hot, setHot] = useState(false);
  const [sel, setSel] = useState<Set<number>>(new Set());
  const [assign, setAssign] = useState<{ ids: number[]; names: string[]; owner?: number } | null>(null);
  const [page, setPage] = useState(1);
  const [size, setSize] = useState(() => { try { return Number(localStorage.getItem('cbs.benchPageSize')) || 25; } catch { return 25; } });

  const load = useCallback(() => { api<Consultant[]>('/consultants', { query: { scope } }).then(setList).catch(e => { setList([]); onError(e); }); }, [scope, onError]);
  useEffect(load, [load]);
  useEffect(() => { setQ(params.get('q') ?? ''); }, [params]);

  const canAssign = can('bench.assign') || can('bench.reassign');
  // Team Lead: consultants given to them that still need a recruiter. Admin / DM / Manager: consultants not with a Team Lead or recruiter yet.
  const needsRecruiter = (c: Consultant) => c.ownerRole === 'Team Lead' && (can('bench.assign') || c.ownerId === user?.id);
  const needsLead = (c: Consultant) => can('bench.assign') && c.ownerRole !== 'Team Lead' && c.ownerRole !== 'Recruiter';
  const toAssign = (c: Consultant) => needsRecruiter(c) || needsLead(c);
  const tabs: [string, string, number][] = [
    ['All', 'All', list?.length ?? 0],
    ...(canAssign ? [['assign', 'To assign', (list ?? []).filter(toAssign).length] as [string, string, number]] : []),
    ...['Bench', 'Marketing', 'Placed'].map(t => [t, t, (list ?? []).filter(c => c.status === t).length] as [string, string, number]),
  ];
  const shown = (list ?? []).filter(c => (tab === 'All' || (tab === 'assign' ? toAssign(c) : c.status === tab)) &&
    (!q || `${c.name} ${c.title} ${c.location} ${c.visa} ${c.ownerName} ${c.skills.join(' ')}`.toLowerCase().includes(q.toLowerCase())));
  // Pages of the filtered list; a new filter or search starts again at page 1.
  useEffect(() => { setPage(1); }, [tab, q, scope]);
  const pages = Math.max(1, Math.ceil(shown.length / size));
  const cur = Math.min(page, pages);
  const pageRows = useMemo(() => shown.slice((cur - 1) * size, cur * size), [shown, cur, size]);

  const remove = async (id: number, name: string, force = false): Promise<void> => {
    if (!force && !await confirm({ title: 'Remove consultant?', message: `${name} will be removed from the bench.`, okLabel: 'Remove', danger: true })) return;
    try {
      await api(`/consultants/${id}`, { method: 'DELETE', query: { force } });
      notify('success', 'Consultant removed', name); load();
    } catch (e) {
      if (e instanceof ApiError && e.code === 'HAS_ACTIVE_SUBMISSIONS' && await confirm({ title: e.title, message: e.detail, okLabel: 'Withdraw & remove', danger: true }))
        return remove(id, name, true);
      onError(e);
    }
  };

  return (
    <>
      <div className="ph"><div><h1>Bench &amp; Hotlist</h1><p>{can('bench.assign') ? 'Every consultant on the bench. Assign them to Team Leads; Team Leads give them to Bench Sales Executives.' : can('bench.reassign') ? "Your team's consultants. Assign the ones given to you to a Bench Sales Executive." : 'Consultants assigned to you. Open one to see the profile and documents, or match them to jobs.'}</p></div>
        <div className="ph-act">
          {can('hotlist.send') && <button className="btn btn-outline" onClick={() => setHot(true)}>Send hotlist</button>}
          {can('parser.use') && <button className="btn btn-outline" onClick={() => nav('/parser')}>✦ AI parse resume</button>}
          {can('bench.create') && <button className="btn btn-dark" onClick={() => setAdding(true)}>+ Add consultant</button>}
        </div></div>
      <div className="card">
        <div className="card-h">
          <div className="tabs">{tabs.map(([t, label, n]) => <button key={t} className={t === tab ? 'on' : ''} onClick={() => { setTab(t); setSel(new Set()); }}>{label} ({n})</button>)}</div>
          <input className="inp" placeholder="Filter by skill, visa, executive…" style={{ maxWidth: 280 }} value={q} onChange={e => { setQ(e.target.value); setParams(e.target.value ? { q: e.target.value } : {}, { replace: true }); }} />
        </div>
        {canAssign && sel.size > 0 && (
          <div className="bulkbar"><b>{sel.size} selected</b>
            <button className="btn btn-sm btn-primary" onClick={() => setAssign({ ids: [...sel], names: (list ?? []).filter(c => sel.has(c.id)).map(c => c.name) })}>
              {can('bench.assign') ? 'Assign to a Team Lead' : 'Assign to an executive'}</button>
            <button className="btn btn-sm btn-outline" onClick={() => setSel(new Set())}>Clear</button></div>)}
        {list === null ? <Empty><Spinner dark /> Loading…</Empty> : (
          <div className="tw"><table><thead><tr>
            {canAssign && <th style={{ width: 34 }}><input type="checkbox" aria-label="Select all on this page" checked={pageRows.length > 0 && pageRows.every(c => sel.has(c.id))}
              onChange={e => { const n = new Set(sel); pageRows.forEach(c => (e.target.checked ? n.add(c.id) : n.delete(c.id))); setSel(n); }} /></th>}
            <th className="slno">Sl No</th><th>Consultant</th><th>Skills</th><th>Visa</th><th>Location</th><th>Exp</th><th>Rate</th><th>Assigned to</th><th>Status</th><th></th></tr></thead><tbody>
            {pageRows.map((c, i) => (
              <tr key={c.id} className={sel.has(c.id) ? 'sel' : ''}>
                {canAssign && <td><input type="checkbox" aria-label={`Select ${c.name}`} checked={sel.has(c.id)} onChange={() => { const n = new Set(sel); if (n.has(c.id)) n.delete(c.id); else n.add(c.id); setSel(n); }} /></td>}
                <td className="slno">{(cur - 1) * size + i + 1}</td>
                <td><Link to={`/bench/${c.id}`} className="who who-link"><Avatar name={c.name} /><div><b>{c.name}</b><small>{c.title} · {c.daysOnBench}d on bench{c.documents ? ` · 📎 ${c.documents}` : ''}</small></div></Link></td>
                <td style={{ minWidth: 200 }}>{c.skills.slice(0, 4).map(s => <span key={s} className="tag">{s}</span>)}{c.skills.length > 4 && <span className="tag s">+{c.skills.length - 4}</span>}</td>
                <td><span className={`tag ${visaClass(c.visa)}`}>{c.visa || '—'}</span></td>
                <td style={{ whiteSpace: 'nowrap' }}>{c.location}{c.relocate && <span className="tag s"> Reloc</span>}</td>
                <td>{c.exp}y</td><td className="mono">{c.rate ? `$${c.rate}/hr` : <span className="muted">—</span>}</td>
                <td style={{ whiteSpace: 'nowrap' }}>{c.ownerName}<br /><small className="muted">{roleLabel(c.ownerRole)}{c.ownerTeam ? ` · ${c.ownerTeam}` : ''}</small>
                  {needsRecruiter(c) && <div><span className="tag y">Assign to an executive</span></div>}
                  {needsLead(c) && <div><span className="tag y">Assign to a Team Lead</span></div>}</td>
                <td><span className={`tag ${statusClass(c.status)}`}>{c.status}</span></td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button className="btn btn-sm btn-outline" onClick={() => nav(`/bench/${c.id}`)}>View</button>{' '}
                  {!can('bench.assign') && can('board.view') && <><button className="btn btn-sm btn-outline" onClick={() => nav(`/external-jobs?consultant=${c.id}`)}>Match jobs</button>{' '}</>}
                  {canAssign && <><button className="btn btn-sm btn-outline" onClick={() => setAssign({ ids: [c.id], names: [c.name], owner: c.ownerId })}>Assign</button>{' '}</>}
                  {can('bench.delete') && <button className="btn btn-sm btn-outline" style={{ color: 'var(--bad)' }} onClick={() => remove(c.id, c.name)}>Delete</button>}
                </td>
              </tr>))}
          </tbody></table>{shown.length === 0 && <Empty>{tab === 'assign' ? 'Everyone is assigned.' : 'No consultants match this filter in your view.'}</Empty>}</div>)}
        {list !== null && <Pager page={cur} pageSize={size} total={shown.length} onPage={setPage}
          onPageSize={n => { setSize(n); setPage(1); try { localStorage.setItem('cbs.benchPageSize', String(n)); } catch { /* storage blocked */ } }} />}
      </div>

      {adding && <ConsultantEditor onClose={() => setAdding(false)} onSaved={id => { setAdding(false); nav(`/bench/${id}`); }} />}
      {hot && <HotlistDialog onClose={() => setHot(false)} />}
      {assign && <AssignDialog ids={assign.ids} names={assign.names} currentOwnerId={assign.owner} onClose={() => setAssign(null)} onDone={() => { setAssign(null); setSel(new Set()); load(); }} />}
    </>
  );
}
