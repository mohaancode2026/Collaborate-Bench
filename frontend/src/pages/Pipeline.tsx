import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { AiText, api, ApiError, ONBOARDING_STAGES, STAGES, Submission } from '../api';
import { useAuth } from '../auth';
import { AiBadge, Avatar, copyText, Empty, Modal, Spinner, useConfirm, useErrorHandler, useToast } from '../ui';

type View = 'grid' | 'board';
const VIEW_KEY = 'cbs.pipelineView';
const savedView = (): View => { try { return localStorage.getItem(VIEW_KEY) === 'board' ? 'board' : 'grid'; } catch { return 'grid'; } };

/** Tag colour per stage: early stages neutral/blue, interviews amber, offer violet, placement green. */
const STAGE_TAG: Record<string, string> = {
  'Submitted': 's', 'Vendor Screen': 'c', 'Client Interview': 'y', 'Offer': 'v', 'MSA / PO': '', 'Billing': 'g',
};
const isStale = (s: Submission) => s.daysInStage >= 3 && s.stage !== 'Billing';
const INTERVIEW = ['Vendor Screen', 'Client Interview'];

type SortKey = 'consultant' | 'requirement' | 'rate' | 'margin' | 'match' | 'stage' | 'days' | 'owner' | 'created';
const SORTERS: Record<SortKey, (a: Submission, b: Submission) => number> = {
  consultant: (a, b) => a.consultantName.localeCompare(b.consultantName),
  requirement: (a, b) => a.title.localeCompare(b.title) || a.client.localeCompare(b.client),
  rate: (a, b) => a.submitRate - b.submitRate,
  margin: (a, b) => (a.marginPerHour ?? -Infinity) - (b.marginPerHour ?? -Infinity),
  match: (a, b) => (a.matchScore ?? -1) - (b.matchScore ?? -1),
  stage: (a, b) => STAGES.indexOf(a.stage) - STAGES.indexOf(b.stage) || b.daysInStage - a.daysInStage,
  days: (a, b) => a.daysInStage - b.daysInStage,
  owner: (a, b) => a.ownerName.localeCompare(b.ownerName),
  created: (a, b) => a.createdUtc.localeCompare(b.createdUtc),
};

function SubmissionGrid({ subs, move, interviewPrep, stage, setStage, q, setQ }: {
  subs: Submission[]; move: (s: Submission, stage: string) => void; interviewPrep: (id: number) => void;
  stage: string; setStage: (s: string) => void; q: string; setQ: (q: string) => void;
}) {
  const { can } = useAuth();
  const [sort, setSort] = useState<{ key: SortKey; dir: 1 | -1 }>({ key: 'stage', dir: 1 });
  const [staleOnly, setStaleOnly] = useState(false);

  const needle = q.trim().toLowerCase();
  const searched = useMemo(() => !needle ? subs : subs.filter(s =>
    [s.consultantName, s.consultantTitle, s.title, s.client, s.vendor, s.ownerName, s.type, s.notes ?? ''].some(f => f.toLowerCase().includes(needle))), [subs, needle]);
  const rows = useMemo(() => searched
    .filter(s => (!stage || s.stage === stage) && (!staleOnly || isStale(s)))
    .sort((a, b) => SORTERS[sort.key](a, b) * sort.dir || b.id - a.id), [searched, stage, staleOnly, sort]);
  const staleCount = searched.filter(s => (!stage || s.stage === stage) && isStale(s)).length;

  // A plain render helper (not a component) so header buttons keep focus across re-sorts.
  const th = (k: SortKey, label: string, num?: boolean) => {
    const on = sort.key === k;
    return <th key={k} aria-sort={on ? (sort.dir === 1 ? 'ascending' : 'descending') : 'none'} className={num ? 'num' : undefined}>
      <button type="button" className={`th-sort${on ? ' on' : ''}`} onClick={() => setSort(on ? { key: k, dir: sort.dir === 1 ? -1 : 1 } : { key: k, dir: k === 'consultant' || k === 'requirement' || k === 'owner' || k === 'stage' ? 1 : -1 })}>
        {label}<span aria-hidden="true">{on ? (sort.dir === 1 ? '▲' : '▼') : '↕'}</span></button></th>;
  };

  return (
    <div className="card">
      <div className="card-h">
        <div className="tabs" aria-label="Filter by stage">
          <button className={!stage ? 'on' : ''} onClick={() => setStage('')}>All ({searched.length})</button>
          {STAGES.map(st => <button key={st} className={stage === st ? 'on' : ''} onClick={() => setStage(st)}>{st} ({searched.filter(s => s.stage === st).length})</button>)}
        </div>
        <div className="flex" style={{ gap: 8 }}>
          <label className={`tag ${staleOnly ? 'y' : 's'} sg-stale`}><input type="checkbox" checked={staleOnly} onChange={e => setStaleOnly(e.target.checked)} /> Stuck 3+ days ({staleCount})</label>
          <input className="inp" type="search" placeholder="Search consultant, job, client, vendor…" aria-label="Search submissions" style={{ width: 280, maxWidth: '100%' }} value={q} onChange={e => setQ(e.target.value)} />
        </div>
      </div>
      <div className="tw"><table className="sub-grid"><thead><tr>
        {th('consultant', 'Consultant')}{th('requirement', 'Requirement')}{th('rate', 'Submit rate', true)}{th('margin', 'Margin', true)}
        {th('match', 'Match', true)}{th('stage', 'Stage')}{th('days', 'In stage', true)}{th('owner', 'Recruiter')}{th('created', 'Submitted')}<th aria-label="Actions" />
      </tr></thead><tbody>
        {rows.map(s => {
          const locked = ONBOARDING_STAGES.includes(s.stage) && !can('onboard.edit');   // shown as a hint; the server enforces it
          return (
            <tr key={s.id} className={isStale(s) ? 'stale' : undefined}>
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
        {rows.length === 0 && <Empty>{subs.length === 0 ? 'No submissions in your view yet.' : 'No submissions match these filters.'}</Empty>}</div>
    </div>
  );
}

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
  const view: View = (params.get('view') as View | null) ?? savedView();

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
  const setParam = (k: string, v: string) => setParams(p => { if (v) p.set(k, v); else p.delete(k); return p; }, { replace: true });
  const setOwner = (id: string) => setParam('owner', id);
  const setView = (v: View) => { setParam('view', v === 'grid' ? '' : v); try { localStorage.setItem(VIEW_KEY, v); } catch { /* storage blocked */ } };
  return (
    <>
      <div className="ph"><div><h1>Submissions Pipeline</h1><p>{view === 'board' ? 'Drag cards between stages, or use the menu on each card.' : 'Sort, search and filter every submission. Change a stage right in the row.'} {!can('onboard.edit') && <><b>MSA / PO</b> and <b>Billing</b> need a Team Lead.</>}</p></div>
        <div className="ph-act">
          <div className="tabs" role="tablist" aria-label="View">
            <button role="tab" aria-selected={view === 'grid'} className={view === 'grid' ? 'on' : ''} onClick={() => setView('grid')}>Grid</button>
            <button role="tab" aria-selected={view === 'board'} className={view === 'board' ? 'on' : ''} onClick={() => setView('board')}>Board</button>
          </div>
          {(owners.length > 1 || ownerId) && <select className="inp" style={{ width: 'auto' }} aria-label="Filter by recruiter" value={ownerId ?? ''} onChange={e => setOwner(e.target.value)}>
            <option value="">All recruiters ({subs.length})</option>
            {owners.map(([id, name]) => <option key={id} value={id}>{name} ({subs.filter(s => s.ownerId === id).length})</option>)}
            {ownerId && !owners.some(([id]) => id === ownerId) && <option value={ownerId}>Selected recruiter (0)</option>}
          </select>}
          {can('match.view') && <Link className="btn btn-dark" to="/match">+ New submission</Link>}
        </div></div>
      {view === 'grid' ? <SubmissionGrid subs={shown} move={move} interviewPrep={interviewPrep}
        stage={params.get('stage') ?? ''} setStage={s => setParam('stage', s)} q={params.get('q') ?? ''} setQ={v => setParam('q', v)} /> :
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
      </div>}
      {prep && <Modal title="AI interview prep" wide onClose={() => setPrep(null)}
        footer={<><button className="btn btn-outline" onClick={() => setPrep(null)}>Close</button>{prep !== 'loading' && <button className="btn btn-primary" onClick={() => copyText(prep.text, notify)}>Copy for consultant</button>}</>}>
        {prep === 'loading' ? <Empty><Spinner dark /> Preparing questions…</Empty> : <><AiBadge source={prep.source} /><div className="jd mt">{prep.text}</div></>}
      </Modal>}
    </>
  );
}
