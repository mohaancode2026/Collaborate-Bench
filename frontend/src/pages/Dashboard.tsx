import { Fragment, useCallback, useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AiText, api, AutopilotSummary, Dashboard as D, download, InboxHome, InboxTask, RecruiterStat } from '../api';
import { useAuth } from '../auth';
import { roleLabel } from '../roles';
import { SubmitDialog } from '../components';
import { Icon, SPARK } from '../Layout';
import { useDueReminders } from '../followups';
import { AiBadge, Avatar, Empty, Ring, Spinner, ago, money, useErrorHandler } from '../ui';
import { TaskRow } from './Inbox';

export default function Dashboard() {
  const { user, scope, can } = useAuth();
  const onError = useErrorHandler();
  const [d, setD] = useState<D | null>(null);
  const [failed, setFailed] = useState(false);
  const [brief, setBrief] = useState<AiText | null | 'loading'>(null);
  const [submit, setSubmit] = useState<[number, number] | null>(null);
  const dueFollowUps = useDueReminders();

  const load = useCallback(() => {
    setFailed(false);
    api<D>('/dashboard', { query: { scope } }).then(setD).catch(e => { setFailed(true); onError(e); });
  }, [scope, onError]);
  useEffect(load, [load]);

  if (failed) return <div className="card"><Empty><b>The dashboard couldn't load.</b><div className="mt"><button className="btn btn-primary" onClick={load}>Try again</button></div></Empty></div>;
  if (!d) return <Empty><Spinner dark /> Loading dashboard…</Empty>;
  const max = Math.max(1, ...d.stages.map(s => s.count));
  // Weekly gross margin is for Admin, Delivery Manager and Manager; Team Leads and recruiters don't see it here.
  const showMargin = d.weeklyMargin != null && user?.role !== 'Team Lead' && user?.role !== 'Recruiter';

  return (
    <>
      <div className="ph">
        <div><h1>Good to see you, {user?.name.split(' ')[0]}</h1><p>{new Date().toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' })}</p></div>
        <div className="ph-act">
          {can('parser.use') && <Link className="btn btn-outline" to="/parser"><Icon d={SPARK} size={16} /> Parse resume</Link>}
          {can('jobs.create') && <Link className="btn btn-dark" to="/jobs?tab=post">+ Post requirement</Link>}
        </div>
      </div>
      {dueFollowUps > 0 && (
        <Link to="/vendor-mail?tab=followups" className="card fu-banner">
          <span>⏰</span><div><b>{dueFollowUps} vendor follow-up{dueFollowUps === 1 ? '' : 's'} due</b><small>Emails waiting on a vendor reply. Open to follow up, snooze or mark done.</small></div><span className="btn btn-sm btn-primary">Review</span>
        </Link>)}
      <div className="grid g4">
        <div className="card kpi dark"><small>Consultants on bench</small><b>{d.bench}</b><span className="trend">{d.marketing} actively marketing</span></div>
        <div className="card kpi"><small>Open requirements</small><b>{d.openRequirements}</b><span className="trend">{d.requirementsToday} added today</span></div>
        <div className="card kpi"><small>Active submissions</small><b>{d.activeSubmissions}</b><span className="trend">{d.inInterviewOrOffer} in interview / offer</span></div>
        {showMargin
          ? <div className="card kpi"><small>Weekly gross margin</small><b>{money(d.weeklyMargin ?? 0)}</b><span className="trend">{d.billing} billing</span></div>
          : <div className="card kpi"><small>Placements</small><b>{d.billing}</b><span className="trend">consultants billing</span></div>}
      </div>

      {can('mail.view') && <HomeTasks />}

      {can('pipeline.view') && <AutopilotCard />}

      <div className="card mt2 brief">
        <div className="card-h"><h3><Icon d={SPARK} size={16} /> AI daily briefing</h3>
          <button className="btn btn-sm btn-dark" disabled={brief === 'loading'} onClick={async () => {
            setBrief('loading');
            try { setBrief(await api<AiText>('/ai/briefing', { method: 'POST', query: { scope } })); } catch (e) { setBrief(null); onError(e); }
          }}>{brief === 'loading' ? <Spinner /> : '✦ Ask AI for a summary'}</button></div>
        <div className="card-b">
          {brief && brief !== 'loading' && <div className="jd" id="briefTxt" style={{ marginBottom: 10 }}><AiBadge source={brief.source} />{'\n'}{brief.text}</div>}
          {d.insights.length ? d.insights.map(i => (
            <div key={i.title} className={`ins ins-${i.type === 'opportunity' ? 'opp' : i.type}`}><span className="ins-d" />
              <div style={{ flex: 1, minWidth: 0 }}><b>{i.title}</b><div className="muted" style={{ fontSize: 13 }}>{i.detail}</div></div>
              <Link className="btn btn-sm btn-outline" to={i.actionPath}>{i.actionLabel}</Link></div>
          )) : <div className="muted">All clear. No risks found in your view today.</div>}
        </div>
      </div>

      {d.byRecruiter?.length > 0 && <RecruiterTable initial={d.byRecruiter} scope={scope} onError={onError} hideMargin={!showMargin} />}

      <div className="grid g21 mt2">
        <div className="card"><div className="card-h"><h3>Top AI matches not yet submitted</h3><Link className="btn btn-sm btn-outline" to="/match">Open Match Agent</Link></div><div className="card-b">
          {d.topMatches.length ? d.topMatches.map(m => (
            <div className="match" key={`${m.consultantId}-${m.requirementId}`}><Ring score={m.score} />
              <div className="mi"><b>{m.consultantName} → {m.requirementTitle}</b><small>{m.client} via {m.vendor} · {m.type} · ${m.billRate}/hr · owner {m.ownerName}</small><div className="why">{m.why}</div></div>
              {can('sub.create') && <button className="btn btn-sm btn-primary" onClick={() => setSubmit([m.consultantId, m.requirementId])}>Submit</button>}
            </div>)) : <Empty>No open matches in your view.</Empty>}
        </div></div>
        <div className="grid" style={{ alignContent: 'start' }}>
          <div className="card"><div className="card-h"><h3>Pipeline by stage</h3><Link className="btn btn-sm btn-outline" to="/pipeline">Board</Link></div><div className="card-b">
            {d.stages.map(s => <div className="bar-row" key={s.stage}><span>{s.stage}</span><div className="bar"><i style={{ width: `${s.count / max * 100}%` }} /></div><b>{s.count}</b></div>)}
          </div></div>
          <div className="card"><div className="card-h"><h3>Team activity</h3></div><div className="card-b" style={{ paddingTop: 6, paddingBottom: 6 }}>
            {d.activity.length ? d.activity.map(a => <div className="feed-i" key={a.id}><Avatar name={a.userName} /><div><b>{a.userName}</b> {a.detail}<small>{ago(a.createdUtc)}</small></div></div>) : <div className="muted">No activity yet.</div>}
          </div></div>
        </div>
      </div>
      {submit && <SubmitDialog consultantId={submit[0]} requirementId={submit[1]} onClose={() => setSubmit(null)} onDone={load} />}
    </>
  );
}

type RangeKey = 'all' | 'today' | 'week' | 'month' | 'last30' | 'quarter' | 'custom';
const RANGES: [RangeKey, string][] = [['all', 'All time'], ['today', 'Today'], ['week', 'This week'], ['month', 'This month'], ['last30', 'Last 30 days'], ['quarter', 'This quarter'], ['custom', 'Custom…']];
const ymd = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const parseYmd = (s: string) => { const [y, m, d] = s.split('-').map(Number); return new Date(y, m - 1, d); };

/** Local-midnight [from, to) for a preset; dates are in the user's own time zone. */
function rangeDates(key: RangeKey, custom: { from: string; to: string }): { from?: Date; to?: Date } {
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const plus = (d: Date, n: number) => { const x = new Date(d); x.setDate(x.getDate() + n); return x; };
  switch (key) {
    case 'today': return { from: today, to: plus(today, 1) };
    case 'week': return { from: plus(today, -((today.getDay() + 6) % 7)), to: plus(today, 1) };   // Monday start
    case 'month': return { from: new Date(today.getFullYear(), today.getMonth(), 1), to: plus(today, 1) };
    case 'last30': return { from: plus(today, -29), to: plus(today, 1) };
    case 'quarter': return { from: new Date(today.getFullYear(), Math.floor(today.getMonth() / 3) * 3, 1), to: plus(today, 1) };
    case 'custom': return { from: custom.from ? parseYmd(custom.from) : undefined, to: custom.to ? plus(parseYmd(custom.to), 1) : undefined };
    default: return {};
  }
}

/** Recruiter-wise report (Admin / Delivery Manager / Team Lead) with a date range. Each name opens that recruiter's submissions. */
function RecruiterTable({ initial, scope, onError, hideMargin }: { initial: RecruiterStat[]; scope: string; onError: (e: unknown) => void; hideMargin?: boolean }) {
  const [range, setRange] = useState<RangeKey>(() => { try { return (sessionStorage.getItem('cbs.recRange') as RangeKey) || 'all'; } catch { return 'all'; } });
  const [custom, setCustom] = useState(() => { const t = new Date(); return { from: ymd(new Date(t.getFullYear(), t.getMonth(), 1)), to: ymd(t) }; });
  const [rows, setRows] = useState<RecruiterStat[]>(initial);
  const [loading, setLoading] = useState(false);
  const [exporting, setExporting] = useState(false);
  const badCustom = range === 'custom' && (!custom.from || !custom.to || custom.from > custom.to);

  useEffect(() => {
    try { sessionStorage.setItem('cbs.recRange', range); } catch { /* storage unavailable */ }
    if (range === 'all') { setRows(initial); return; }
    if (badCustom) return;
    const { from, to } = rangeDates(range, custom);
    let live = true;
    setLoading(true);
    api<RecruiterStat[]>('/dashboard/recruiters', { query: { scope, from: from?.toISOString(), to: to?.toISOString() } })
      .then(r => { if (live) setRows(r); })
      .catch(e => { if (live) onError(e); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [range, custom, scope, initial, badCustom, onError]);

  const { from, to } = rangeDates(range, custom);
  const fmt = (d: Date) => d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  const caption = range === 'all' ? 'all time' : from && to ? `${fmt(from)} – ${fmt(new Date(to.getTime() - 1))}` : '';
  const stageCols: [string, string][] = [['Submitted', 'Submitted'], ['Vendor Screen', 'Vendor screen'], ['Client Interview', 'Interview'], ['Offer', 'Offer'], ['MSA / PO', 'MSA / PO']];
  const showMargin = !hideMargin && rows.some(r => r.weeklyMargin != null);
  const n = (r: RecruiterStat, stage: string) => r.stages.find(s => s.stage === stage)?.count ?? 0;
  const cell = (v: number) => <td className="mono" style={{ textAlign: 'center', color: v ? undefined : 'var(--muted)' }}>{v}</td>;
  const total = (f: (r: RecruiterStat) => number) => rows.reduce((a, r) => a + f(r), 0);
  const c = { textAlign: 'center' } as const;
  const exportExcel = async () => {
    setExporting(true);
    try {
      await download('/dashboard/recruiters/export', {
        scope, from: from?.toISOString(), to: to?.toISOString(),
        range: range === 'all' ? 'All time' : caption, tzOffset: new Date().getTimezoneOffset(),
      }, 'recruiter-report.xlsx');
    } catch (e) { onError(e); } finally { setExporting(false); }
  };
  return (
    <div className="card mt2">
      <div className="card-h" style={{ flexWrap: 'wrap', gap: 10 }}>
        <div><h3>Executive-wise submissions</h3><small className="muted">Submissions created {caption}{loading && ' · updating…'} · bench is the current count</small></div>
        <div className="flex" style={{ gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
          <select className="inp" style={{ width: 'auto' }} aria-label="Date range" value={range} onChange={e => setRange(e.target.value as RangeKey)}>
            {RANGES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
          </select>
          {range === 'custom' && <>
            <input type="date" className="inp" style={{ width: 'auto' }} aria-label="From date" value={custom.from} max={custom.to || undefined} onChange={e => setCustom(x => ({ ...x, from: e.target.value }))} />
            <span className="muted">to</span>
            <input type="date" className="inp" style={{ width: 'auto' }} aria-label="To date" value={custom.to} min={custom.from || undefined} onChange={e => setCustom(x => ({ ...x, to: e.target.value }))} />
          </>}
          <button className="btn btn-sm btn-dark" onClick={exportExcel} disabled={exporting || badCustom || loading}
            title="Download this report (and the submissions behind it) as an Excel file">
            {exporting ? <Spinner /> : '⬇ Export to Excel'}</button>
          <Link className="btn btn-sm btn-outline" to="/pipeline">All submissions</Link>
        </div>
      </div>
      {badCustom && <div className="card-b" style={{ paddingBottom: 0 }}><span className="tag r">Pick a start date that is on or before the end date.</span></div>}
      <div className="tw" style={{ opacity: loading ? 0.6 : 1, transition: 'opacity .15s' }}><table>
        <thead><tr>
          <th>Bench Sales Executive</th><th style={c}>Bench</th><th style={c}>Total</th>
          {stageCols.map(([, label]) => <th key={label} style={c}>{label}</th>)}
          <th style={c}>Active</th><th style={c}>Placed</th><th style={c}>Stuck 3d+</th>
          {showMargin && <th style={{ textAlign: 'right' }}>Margin / wk</th>}
          <th>Last submission</th>
        </tr></thead>
        <tbody>
          {rows.map(r => (
            <tr key={r.userId}>
              <td><div className="flex" style={{ gap: 8, flexWrap: 'nowrap' }}><Avatar name={r.name} size={28} />
                <div style={{ minWidth: 0 }}><Link to={`/pipeline?owner=${r.userId}`}><b>{r.name}</b></Link>
                  <br /><small className="muted">{roleLabel(r.role)}{r.team ? ` · ${r.team}` : ''}</small></div></div></td>
              {cell(r.bench)}
              <td className="mono" style={c}><b>{r.total}</b></td>
              {stageCols.map(([stage]) => <Fragment key={stage}>{cell(n(r, stage))}</Fragment>)}
              {cell(r.active)}{cell(r.placed)}
              <td style={c}>{r.stale ? <span className="tag y">{r.stale}</span> : <span className="muted mono">0</span>}</td>
              {showMargin && <td className="mono" style={{ textAlign: 'right' }}>{money(r.weeklyMargin ?? 0)}</td>}
              <td style={{ whiteSpace: 'nowrap' }}>{r.lastSubmissionUtc ? ago(r.lastSubmissionUtc) : <span className="muted">None in range</span>}</td>
            </tr>))}
        </tbody>
        <tfoot><tr>
          <td><b>Total</b></td>{cell(total(r => r.bench))}
          <td className="mono" style={c}><b>{total(r => r.total)}</b></td>
          {stageCols.map(([stage]) => <Fragment key={stage}>{cell(total(r => n(r, stage)))}</Fragment>)}
          {cell(total(r => r.active))}{cell(total(r => r.placed))}{cell(total(r => r.stale))}
          {showMargin && <td className="mono" style={{ textAlign: 'right' }}><b>{money(total(r => r.weeklyMargin ?? 0))}</b></td>}
          <td />
        </tr></tfoot>
      </table></div>
    </div>
  );
}

/**
 * Autopilot at a glance: on/off, last run, and what is waiting at each step (in the user's view).
 * Steps that need a person (approvals, vendor replies, problems) are highlighted and link straight to their tab.
 */
function AutopilotCard() {
  const [s, setS] = useState<AutopilotSummary | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let alive = true;
    const load = () => api<AutopilotSummary>('/autopilot/summary').then(x => { if (alive) { setS(x); setFailed(false); } }).catch(() => { if (alive) setFailed(true); });
    load();
    window.addEventListener('cbs:autopilot', load);
    return () => { alive = false; window.removeEventListener('cbs:autopilot', load); };
  }, []);
  if (failed) return null;   // the dashboard still works without this card
  const attention = s ? s.needsResume + s.failed : 0;
  const steps: [string, number | undefined, string, boolean][] = [
    ['Found, being prepared', s?.found, 'approval', false],
    ['Need your approval', s?.awaitingApproval, 'approval', true],
    ['Waiting on consultant RTR', s?.awaitingRtr, 'rtr', false],
    ['Submitted (7 days)', s?.submittedThisWeek, 'submitted', false],
    ['Vendor replies to review', s?.repliesToReview, 'replies', true],
  ];
  return (
    <div className="card mt2 dash-ap">
      <div className="card-h">
        <h3>⚡ Autopilot <span className={`tag ${s?.enabled ? 'g' : 's'}`} style={{ marginLeft: 6 }}>{s ? (s.enabled ? 'On' : 'Off') : '…'}</span></h3>
        <div className="flex">
          <span className="muted" style={{ fontSize: 13 }}>{s?.lastRunUtc ? `Last run ${ago(s.lastRunUtc)}${s.lastRunSummary ? ` · ${s.lastRunSummary}` : ''}` : s?.enabled ? 'First run pending' : ''}</span>
          <Link className="btn btn-sm btn-outline" to="/autopilot">Open Autopilot</Link>
        </div>
      </div>
      <div className="card-b">
        {s && !s.enabled && s.awaitingApproval + s.awaitingRtr + s.repliesToReview === 0
          ? <p className="muted" style={{ margin: 0 }}>The Autopilot is off. When it's on, it finds matches for your bench, tailors resumes and drafts submissions for you to approve, then follows up with vendors automatically.</p>
          : <div className="dash-ap-steps">
              {steps.map(([label, n, tab, action]) => (
                <Link key={label} to={`/autopilot?tab=${tab}`} className={`dash-ap-step${action && n ? ' act' : ''}`}>
                  <b>{n ?? '…'}</b><span>{label}</span>
                </Link>))}
            </div>}
        {attention > 0 && <Link to="/autopilot?tab=attention" className="dash-ap-warn">⚠ {attention} need attention (missing resume or a failed send). Review →</Link>}
      </div>
    </div>
  );
}

/**
 * The executive's home: tasks from the AI Inbox with their due time (each reminder opens a ready draft), and upcoming
 * interviews: yours, plus your team's for Team Leads. A full calendar view comes later.
 */
function HomeTasks() {
  const nav = useNavigate();
  const onError = useErrorHandler();
  const [h, setH] = useState<InboxHome | null>(null);
  const load = useCallback(() => { api<InboxHome>('/inbox/home').then(setH).catch(() => setH({ tasks: [], interviews: [], overdue: 0 })); }, []);
  useEffect(() => {
    load();
    const t = setInterval(load, 60_000);
    window.addEventListener('cbs:inbox', load);
    return () => { clearInterval(t); window.removeEventListener('cbs:inbox', load); };
  }, [load]);
  const done = async (t: InboxTask) => { try { await api(`/inbox/tasks/${t.id}`, { method: 'PUT', body: { status: t.status === 'Open' ? 'Done' : 'Open' } }); load(); } catch (e) { onError(e); } };
  const edit = () => nav('/inbox?view=tasks');
  if (!h) return null;
  return (
    <div className="grid g2 mt2 home-tasks">
      <div className="card">
        <div className="card-h"><h3>My tasks {h.overdue > 0 && <span className="tag r">{h.overdue} overdue</span>}</h3><Link className="btn btn-sm btn-outline" to="/inbox?view=tasks">All tasks</Link></div>
        <div className="card-b">{h.tasks.length === 0 ? <p className="muted" style={{ margin: 0 }}>Nothing due. Follow-ups and tasks from your emails show up here with their due time.</p>
          : h.tasks.map(t => <TaskRow key={t.id} t={t} onToggle={() => done(t)} onEdit={edit} onDone={load} onOpenEmail={t.inboxItemId ? () => nav(`/inbox?open=${t.inboxItemId}`) : undefined} />)}</div>
      </div>
      <div className="card">
        <div className="card-h"><h3>Upcoming interviews</h3><Link className="btn btn-sm btn-outline" to="/inbox?view=interviews">Interview emails</Link></div>
        <div className="card-b">{h.interviews.length === 0 ? <p className="muted" style={{ margin: 0 }}>No interviews scheduled. Interview requests from vendors appear here as "Interview to schedule".</p>
          : h.interviews.map(t => <TaskRow key={t.id} t={t} showOwner onToggle={() => done(t)} onEdit={edit} onDone={load} onOpenEmail={t.inboxItemId ? () => nav(`/inbox?open=${t.inboxItemId}`) : undefined} />)}</div>
      </div>
    </div>
  );
}
