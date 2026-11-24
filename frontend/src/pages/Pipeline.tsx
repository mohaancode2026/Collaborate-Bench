import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { AiText, api, ApiError, ONBOARDING_STAGES, STAGES, Submission } from '../api';
import { useAuth } from '../auth';
import { Pager } from '../pager';
import { AiBadge, Avatar, copyText, Empty, Modal, Spinner, useConfirm, useErrorHandler, useToast } from '../ui';

type View = 'grid' | 'board';
const VIEW_KEY = 'cbs.pipelineView';
const SIZE_KEY = 'cbs.pipelinePageSize';
const savedView = (): View => { try { return localStorage.getItem(VIEW_KEY) === 'board' ? 'board' : 'grid'; } catch { return 'grid'; } };
const savedSize = () => { try { return Number(localStorage.getItem(SIZE_KEY)) || 25; } catch { return 25; } };

/** Tag colour per stage: early stages neutral/blue, interviews amber, offer violet, placement green. */
const STAGE_TAG: Record<string, string> = {
  'Submitted': 's', 'Vendor Screen': 'c', 'Client Interview': 'y', 'Offer': 'v', 'MSA / PO': '', 'Billing': 'g',
};
const isStale = (s: Submission) => s.daysInStage >= 3 && s.stage !== 'Billing';
const INTERVIEW = ['Vendor Screen', 'Client Interview'];

type SortKey = 'consultant' | 'requirement' | 'rate' | 'margin' | 'match' | 'stage' | 'days' | 'owner' | 'created';
interface OwnerCount { id: number; name: string; count: number; }
interface SubmissionPage { items: Submission[]; total: number; page: number; pageSize: number; stageCounts: Record<string, number>; staleCount: number; owners: OwnerCount[]; }

/** Waits until the user stops typing before searching (one request per pause, not per key). */
function useDebounced<T>(value: T, ms: number) {
  const [v, setV] = useState(value);
  useEffect(() => { const t = setTimeout(() => setV(value), ms); return () => clearTimeout(t); }, [value, ms]);
  return v;
}

/** The grid: one page at a time from the server (search, filters, sorting and counts are done in the database). */
function SubmissionGrid({ move, interviewPrep, reloadKey, onOwners }: {
  move: (s: Submission, stage: string) => Promise<void>; interviewPrep: (id: number) => void; reloadKey: number;
  onOwners: (o: OwnerCount[], total: number) => void;
}) {
  const { can, scope } = useAuth();
  const onError = useErrorHandler();
  const [params, setParams] = useSearchParams();
  const stage = params.get('stage') ?? '';
  const owner = params.get('owner') ?? '';
  const staleOnly = params.get('stale') === '1';
  const sortKey = (params.get('sort') as SortKey) || 'stage';
  const dir = params.get('dir') === 'desc' ? 'desc' : 'asc';
  const page = Math.max(1, Number(params.get('page')) || 1);
  const [size, setSize] = useState(savedSize);
  const [qInput, setQInput] = useState(params.get('q') ?? '');
  const q = useDebounced(qInput.trim(), 350);
  const [data, setData] = useState<SubmissionPage | null>(null);
  const [busy, setBusy] = useState(false);
  const seq = useRef(0);

  const set = useCallback((changes: Record<string, string>, resetPage = true) => setParams(p => {
    Object.entries(changes).forEach(([k, v]) => (v ? p.set(k, v) : p.delete(k)));
    if (resetPage) p.delete('page');
    return p;
  }, { replace: true }), [setParams]);
  useEffect(() => { if ((params.get('q') ?? '') !== q) set({ q }); }, [q]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    const mine = ++seq.current;   // ignore answers that arrive after a newer request
    setBusy(true);
    api<SubmissionPage>('/submissions/page', { query: { scope, stage: stage || undefined, q: q || undefined, owner: owner || undefined, stale: staleOnly || undefined, sort: sortKey, dir, page, pageSize: size } })
      .then(r => { if (mine === seq.current) { setData(r); onOwners(r.owners, r.owners.reduce((a, o) => a + o.count, 0)); } })
      .catch(e => { if (mine === seq.current) { onError(e); setData(d => d ?? { items: [], total: 0, page: 1, pageSize: size, stageCounts: {}, staleCount: 0, owners: [] }); } })
      .finally(() => { if (mine === seq.current) setBusy(false); });
  }, [scope, stage, q, owner, staleOnly, sortKey, dir, page, size, reloadKey, onError, onOwners]);

  // A plain render helper (not a component) so header buttons keep focus across re-sorts.
  const th = (k: SortKey, label: string, num?: boolean) => {
    const on = sortKey === k;
    const firstDir = k === 'consultant' || k === 'requirement' || k === 'owner' || k === 'stage' ? 'asc' : 'desc';
    return <th key={k} aria-sort={on ? (dir === 'asc' ? 'ascending' : 'descending') : 'none'} className={num ? 'num' : undefined}>
      <button type="button" className={`th-sort${on ? ' on' : ''}`} onClick={() => set({ sort: k, dir: on ? (dir === 'asc' ? 'desc' : 'asc') : firstDir })}>
        {label}<span aria-hidden="true">{on ? (dir === 'asc' ? '▲' : '▼') : '↕'}</span></button></th>;
  };

  const counts = data?.stageCounts ?? {};
  const all = Object.values(counts).reduce((a, n) => a + n, 0);
  const rows = data?.items ?? [];
  return (
    <div className="card">
      <div className="card-h">
        <div className="tabs" aria-label="Filter by stage">
          <button className={!stage ? 'on' : ''} onClick={() => set({ stage: '' })}>All ({all})</button>
          {STAGES.map(st => <button key={st} className={stage === st ? 'on' : ''} onClick={() => set({ stage: st })}>{st} ({counts[st] ?? 0})</button>)}
        </div>
        <div className="flex" style={{ gap: 8 }}>
          <label className={`tag ${staleOnly ? 'y' : 's'} sg-stale`}><input type="checkbox" checked={staleOnly} onChange={e => set({ stale: e.target.checked ? '1' : '' })} /> Stuck 3+ days ({data?.staleCount ?? 0})</label>
          <input className="inp" type="search" placeholder="Search consultant, job, client, vendor…" aria-label="Search submissions" style={{ width: 280, maxWidth: '100%' }} value={qInput} onChange={e => setQInput(e.target.value)} />
        </div>
      </div>
      {data === null ? <Empty><Spinner dark /> Loading submissions…</Empty> : <>
        <div className="tw" style={{ opacity: busy ? 0.6 : 1, transition: 'opacity .15s' }}><table className="sub-grid"><thead><tr>
          <th className="slno">Sl No</th>{th('consultant', 'Consultant')}{th('requirement', 'Requirement')}{th('rate', 'Submit rate', true)}{th('margin', 'Margin', true)}
          {th('match', 'Match', true)}{th('stage', 'Stage')}{th('days', 'In stage', true)}{th('owner', 'Executive')}{th('created', 'Submitted')}<th aria-label="Actions" />
        </tr></thead><tbody>
          {rows.map((s, i) => {
            const locked = ONBOARDING_STAGES.includes(s.stage) && !can('onboard.edit');   // shown as a hint; the server enforces it
            return (
              <tr key={s.id} className={isStale(s) ? 'stale' : undefined}>
                <td className="slno">{(data.page - 1) * data.pageSize + i + 1}</td>
                <td><div className="who"><Avatar name={s.consultantName} /><div><b>{s.consultantName}</b><small>{s.consultantTitle}</small></div></div></td>
                <td style={{ minWidth: 200 }}><b className="sg-title">{s.title}</b><small className="muted">{s.client} · {s.vendor} · {s.type}</small></td>
                <td className="num mono">${s.submitRate}/hr{s.rtr && <> <span className="tag g" title="Right to represent signed">RTR</span></>}</td>
                <td className="num mono">{s.marginPerHour != null ? <span style={{ color: s.marginPerHour < 0 ? 'var(--bad)' : undefined }}>${s.marginPerHour}/hr</span> : <span className="muted">—</span>}</td>
                <td className="num">{s.matchScore != null ? <span className={`tag ${s.matchScore >= 80 ? 'g' : s.matchScore >= 60 ? 'y' : 's'}`}>{s.matchScore}%</span> : <span className="muted">—</span>}</td>
                <td>{can('sub.move')
                  ? <select className={`stage-sel tag ${STAGE_TAG[s.stage] ?? ''}`} aria-label={`Stage for ${s.consultantName}`} title={locked ? 'MSA / PO and Billing changes need a Team Lead' : undefined} value={s.stage} onChange={e => move(s, e.target.value)}>
                      {STAGES.map(x => <option key={x}>{x}</option>)}{can('sub.withdraw') && <option value="__withdraw">Withdraw…</option>}</select>
                  : <span className={`tag ${STAGE_TAG[s.stage] ?? ''}`} title={locked ? 'Needs a Team Lead to change' : undefined}>{s.stage}{locked && ' 🔒'}</span>}</td>
                <td className="num">{isStale(s) ? <span className="tag y">{s.daysInStage}d</span> : <span className="muted">{s.daysInStage}d</span>}</td>
                <td style={{ whiteSpace: 'nowrap' }}><div className="flex" style={{ gap: 6, flexWrap: 'nowrap' }}><Avatar name={s.ownerName} size={22} />{s.ownerName}</div></td>
                <td style={{ whiteSpace: 'nowrap' }} title={new Date(s.createdUtc).toLocaleString()}>{new Date(s.createdUtc).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  {INTERVIEW.includes(s.stage) && <button className="btn btn-sm btn-outline" onClick={() => interviewPrep(s.id)}>✦ Interview prep</button>}
                  {s.notes && <span className="sg-note" title={s.notes} aria-label={`Notes: ${s.notes}`}>📝</span>}
                </td>
              </tr>);
          })}
        </tbody></table>
          {rows.length === 0 && <Empty>{all === 0 && !q && !owner ? 'No submissions in your view yet.' : 'No submissions match these filters.'}</Empty>}</div>
        <Pager page={data.page} pageSize={data.pageSize} total={data.total} busy={busy}
          onPage={p => set({ page: p > 1 ? String(p) : '' }, false)}
          onPageSize={n => { setSize(n); try { localStorage.setItem(SIZE_KEY, String(n)); } catch { /* storage blocked */ } set({}); }} />
      </>}
    </div>
  );
}

export default function Pipeline() {
  const { scope, can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [params, setParams] = useSearchParams();
  const view: View = (params.get('view') as View | null) ?? savedView();
  const ownerId = Number(params.get('owner')) || null;   // executive filter, also set from the dashboard table
  // Board: the whole pipeline (a kanban needs every card), loaded only when the board is shown.
  const [subs, setSubs] = useState<Submission[] | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [owners, setOwners] = useState<{ list: OwnerCount[]; total: number }>({ list: [], total: 0 });
  const [drag, setDrag] = useState<number | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const [prep, setPrep] = useState<AiText | 'loading' | null>(null);

  const loadBoard = useCallback(() => { api<Submission[]>('/submissions', { query: { scope } }).then(setSubs).catch(e => { setSubs([]); onError(e); }); }, [scope, onError]);
  useEffect(() => { if (view === 'board') loadBoard(); }, [view, loadBoard]);
  const reload = () => { if (view === 'board') loadBoard(); else setReloadKey(k => k + 1); };
  const gridOwners = useCallback((list: OwnerCount[], total: number) => setOwners({ list, total }), []);

  const move = async (s: Submission, stage: string) => {
    if (stage === s.stage) return;
    if (stage === '__withdraw') {
      if (!await confirm({ title: 'Withdraw submission?', message: `${s.consultantName} will be withdrawn from "${s.title}". Tell the vendor as well.`, okLabel: 'Withdraw', danger: true })) return;
      try { await api(`/submissions/${s.id}`, { method: 'DELETE' }); notify('success', 'Submission withdrawn'); reload(); } catch (e) { onError(e); }
      return;
    }
    // optimistic update on the board, rolled back by the reload on error
    setSubs(xs => xs?.map(x => (x.id === s.id ? { ...x, stage } : x)) ?? null);
    try {
      await api(`/submissions/${s.id}/stage`, { method: 'POST', body: { toStage: stage, rowVersion: s.rowVersion } });
      notify('success', stage === 'Billing' ? `🎉 ${s.consultantName} is placed` : 'Stage updated', `${s.consultantName} → ${stage}`);
    } catch (e) {
      onError(e);
      if (e instanceof ApiError && e.code === 'ONBOARDING_INCOMPLETE') notify('info', 'Finish onboarding first', 'Open Onboarding & Billing to complete the checklist.');
    } finally { reload(); }
  };

  const interviewPrep = async (id: number) => {
    setPrep('loading');
    try { setPrep(await api<AiText>(`/ai/interview-prep/${id}`, { method: 'POST' })); } catch (e) { setPrep(null); onError(e); }
  };

  const setParam = (k: string, v: string) => setParams(p => { if (v) p.set(k, v); else p.delete(k); p.delete('page'); return p; }, { replace: true });
  const setView = (v: View) => { setParam('view', v === 'grid' ? '' : v); try { localStorage.setItem(VIEW_KEY, v); } catch { /* storage blocked */ } };
  const boardShown = view === 'board' && subs ? (ownerId ? subs.filter(s => s.ownerId === ownerId) : subs) : [];
  const ownerList: OwnerCount[] = view === 'board'
    ? [...new Map((subs ?? []).map(s => [s.ownerId, s.ownerName])).entries()].map(([id, name]) => ({ id, name, count: (subs ?? []).filter(s => s.ownerId === id).length })).sort((a, b) => a.name.localeCompare(b.name))
    : owners.list;
  const ownerTotal = view === 'board' ? subs?.length ?? 0 : owners.total;
  return (
    <>
      <div className="ph"><div><h1>Submissions Pipeline</h1><p>{view === 'board' ? 'Drag cards between stages, or use the menu on each card.' : 'Sort, search and filter every submission. Change a stage right in the row.'} {!can('onboard.edit') && <><b>MSA / PO</b> and <b>Billing</b> need a Team Lead.</>}</p></div>
        <div className="ph-act">
          <div className="tabs" role="tablist" aria-label="View">
            <button role="tab" aria-selected={view === 'grid'} className={view === 'grid' ? 'on' : ''} onClick={() => setView('grid')}>Grid</button>
            <button role="tab" aria-selected={view === 'board'} className={view === 'board' ? 'on' : ''} onClick={() => setView('board')}>Board</button>
          </div>
          {(ownerList.length > 1 || ownerId) && <select className="inp" style={{ width: 'auto' }} aria-label="Filter by executive" value={ownerId ?? ''} onChange={e => setParam('owner', e.target.value)}>
            <option value="">All executives ({ownerTotal})</option>
            {ownerList.map(o => <option key={o.id} value={o.id}>{o.name} ({o.count})</option>)}
            {ownerId && !ownerList.some(o => o.id === ownerId) && <option value={ownerId}>Selected executive (0)</option>}
          </select>}
          {can('board.view') && <Link className="btn btn-dark" to="/external-jobs">+ New submission</Link>}
        </div></div>
      {view === 'grid' ? <SubmissionGrid move={move} interviewPrep={interviewPrep} reloadKey={reloadKey} onOwners={gridOwners} />
        : !subs ? <Empty><Spinner dark /> Loading pipeline…</Empty> : (
      <div className="kanban">
        {STAGES.map(st => {
          const items = boardShown.filter(s => s.stage === st);
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
      </div>)}
      {prep && <Modal title="AI interview prep" wide onClose={() => setPrep(null)}
        footer={<><button className="btn btn-outline" onClick={() => setPrep(null)}>Close</button>{prep !== 'loading' && <button className="btn btn-primary" onClick={() => copyText(prep.text, notify)}>Copy for consultant</button>}</>}>
        {prep === 'loading' ? <Empty><Spinner dark /> Preparing questions…</Empty> : <><AiBadge source={prep.source} /><div className="jd mt">{prep.text}</div></>}
      </Modal>}
    </>
  );
}
