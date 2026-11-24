import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { api, ApiError, Consultant, download, fetchFile, InboxAttachment, InboxDetail, InboxDraft, InboxItem, InboxMatch, InboxSummary, InboxTask } from '../api';
import { useAuth } from '../auth';
import { TaskDraftButton } from '../taskDraft';
import { ComposeDialog, ComposeInitial } from '../compose';
import { inWorkday, InboxAlertPrefs, loadAlertPrefs, playChime, saveAlertPrefs } from '../inboxWatch';
import { AiBadge, Alert, ago, Empty, Field, FieldErrors, Modal, Ring, Spinner, useErrorHandler, useToast } from '../ui';

type View = 'all' | 'high' | 'jobs' | 'matches' | 'reply' | 'interviews' | 'tasks' | 'sent' | 'awaiting' | 'noise' | 'done';
const VIEWS: [View, string][] = [['all', 'Inbox'], ['high', 'High priority'], ['jobs', 'Job emails'], ['matches', 'Matched profiles'], ['reply', 'Needs reply'], ['interviews', 'Interviews'], ['tasks', 'Tasks'], ['sent', 'Sent'], ['awaiting', 'Awaiting reply'], ['noise', 'Noise'], ['done', 'Done']];
const CAT: Record<InboxItem['category'], [string, string]> = {
  JobListing: ['Job', 'v'], Interview: ['Interview', 'g'], FollowUp: ['Follow-up', 'y'], Rate: ['Rate', 'c'], Rejection: ['Rejection', 's'], Other: ['Other', 's'], Noise: ['Noise', 's'], Sent: ['Sent', 's'],
};
const daysSince = (iso: string) => Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000));
const PRI: Record<InboxItem['priority'], string> = { High: 'r', Medium: 'y', Low: 's' };
const KIND: Record<InboxTask['kind'], string> = { Task: 'Task', FollowUp: 'Follow-up', Interview: 'Interview' };
const INTERVIEW_TYPES = ['Client call', 'Technical call', 'L1', 'L2', 'Final', 'Other'];

/** ISO (UTC) ↔ the value of an <input type="datetime-local"> in the browser's time zone. */
const toLocal = (iso?: string | null) => {
  if (!iso) return '';
  const d = new Date(iso);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
};
const fromLocal = (v: string) => (v ? new Date(v).toISOString() : undefined);
const when = (iso?: string | null) => (iso ? new Date(iso).toLocaleString(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }) : 'No date');
const overdue = (t: InboxTask) => !!t.dueUtc && t.status === 'Open' && new Date(t.dueUtc).getTime() < Date.now();

export default function Inbox() {
  const { can } = useAuth();
  const [params, setParams] = useSearchParams();
  const onError = useErrorHandler();
  const notify = useToast();
  const view = (params.get('view') as View) || 'all';
  const openId = Number(params.get('open')) || 0;
  const [summary, setSummary] = useState<InboxSummary | null>(null);
  const [items, setItems] = useState<InboxItem[] | null>(null);
  const [tasks, setTasks] = useState<InboxTask[] | null>(null);
  const [q, setQ] = useState('');
  const [fetching, setFetching] = useState(false);
  const [alerts, setAlerts] = useState(false);
  const [taskEdit, setTaskEdit] = useState<{ task?: InboxTask; draft: Partial<InboxTask> } | null>(null);

  const setView = (v: View) => { const n = new URLSearchParams(params); n.set('view', v); n.delete('open'); setParams(n, { replace: true }); };
  const open = (id: number | null) => { const n = new URLSearchParams(params); if (id) n.set('open', String(id)); else n.delete('open'); setParams(n, { replace: true }); };

  const loadSummary = useCallback(() => api<InboxSummary>('/inbox/summary').then(setSummary).catch(onError), [onError]);
  // quiet = background refresh: keep what's on screen until the new list arrives, and don't pop errors.
  const loadList = useCallback((quiet = false) => {
    if (view === 'tasks') { if (!quiet) setTasks(null); api<InboxTask[]>('/inbox/tasks').then(setTasks).catch(e => { if (!quiet) { setTasks([]); onError(e); } }); return; }
    if (!quiet) setItems(null);
    api<InboxItem[]>('/inbox', { query: { view, q: q || undefined } }).then(setItems).catch(e => { if (!quiet) { setItems([]); onError(e); } });
  }, [view, q, onError]);
  const refresh = useCallback(() => { loadSummary(); loadList(); window.dispatchEvent(new Event('cbs:inbox')); }, [loadSummary, loadList]);
  useEffect(() => { loadSummary(); }, [loadSummary]);
  useEffect(() => { const t = setTimeout(loadList, q ? 300 : 0); return () => clearTimeout(t); }, [loadList, q]);
  // The server reads Outlook every couple of minutes by itself; the page just picks up what's new.
  useEffect(() => {
    const tick = () => { if (document.visibilityState === 'visible') { loadSummary(); loadList(true); } };
    const t = setInterval(tick, 45_000);
    const h = () => { loadSummary(); loadList(true); };
    window.addEventListener('cbs:inbox', h);
    document.addEventListener('visibilitychange', tick);
    return () => { clearInterval(t); window.removeEventListener('cbs:inbox', h); document.removeEventListener('visibilitychange', tick); };
  }, [loadSummary, loadList]);

  /** Re-match recent job emails against the bench, using each consultant's skills and latest resume, then show the matches. */
  const fetchMatches = async () => {
    setFetching(true);
    try {
      const r = await api<{ jobs: number; withMatches: number; consultants: number }>('/inbox/rematch', { method: 'POST' });
      notify(r.withMatches ? 'success' : 'info', r.withMatches ? `${r.withMatches} job email${r.withMatches === 1 ? '' : 's'} with matching profiles` : 'No matching profiles yet',
        `Checked ${r.jobs} job email${r.jobs === 1 ? '' : 's'} from the last 14 days against ${r.consultants} consultant${r.consultants === 1 ? '' : 's'} and their resumes.`);
      setView('matches'); loadSummary();
    } catch (e) { onError(e); } finally { setFetching(false); }
  };

  const tiles: { v: View; label: string; n?: number; hot?: boolean; hint: string }[] = [
    { v: 'high', label: 'High priority', n: summary?.high, hot: !!summary?.high, hint: summary?.high ? 'Needs you now' : 'Nothing urgent' },
    { v: 'jobs', label: 'Job emails', n: summary?.jobs, hint: 'Matched to your consultants' },
    { v: 'reply', label: 'Needs reply', n: summary?.needsReply, hint: summary?.awaitingReply ? `+ ${summary.awaitingReply} sent, no answer yet` : 'AI drafts the follow-up' },
    { v: 'tasks', label: 'Tasks due (24h)', n: summary?.tasksDue, hint: `${summary?.interviews ?? 0} interview${summary?.interviews === 1 ? '' : 's'} open` },
  ];

  return (
    <>
      <div className="ph"><div><h1>AI Inbox</h1><p>Your Outlook inbox, read by AI: job emails matched to your consultants, follow-ups drafted, tasks and interviews pulled out.</p></div>
        <div className="ph-act">
          <button className="btn btn-outline" onClick={() => setAlerts(true)}>🔔 Alerts</button>
          <button className="btn btn-outline" onClick={() => setTaskEdit({ draft: { kind: 'Task', title: '' } })}>+ Task</button>
          <button className="btn btn-primary" onClick={fetchMatches} disabled={fetching}>{fetching ? <><Spinner /> Matching…</> : '✦ Fetch matching profiles'}</button>
        </div></div>

      {summary && !summary.configured && <Alert type="warning" title="Outlook isn't set up on the server">Ask your admin to configure Microsoft sign-in (Microsoft:TenantId, ClientId, ClientSecret).</Alert>}
      {summary?.configured && !summary.connected && (
        <Alert type="info" title="Connect your Outlook to see your AI Inbox">Sign in with Microsoft, or connect Outlook on <Link to="/vendor-mail">Vendor Emails</Link>. The AI reads new Inbox mail every sync.</Alert>)}

      <div className="grid g4 inbox-tiles">
        {tiles.map(t => (
          <button key={t.v} className={`card kpi inbox-tile${t.hot ? ' hot' : ''}${view === t.v ? ' sel' : ''}`} onClick={() => setView(t.v)}>
            <small>{t.label}</small><b>{t.n ?? '—'}</b><span className="trend">{t.hint}</span>
          </button>))}
      </div>
      {summary?.connected && <p className="live-note"><i className="live-dot" /> Live: new Outlook mail shows up here by itself{summary.lastSyncUtc ? ` · last read ${ago(summary.lastSyncUtc)}` : ''}</p>}

      <div className="card">
        <div className="card-h">
          <div className="tabs">{VIEWS.map(([v, l]) => <button key={v} className={v === view ? 'on' : ''} onClick={() => setView(v)}>{l}</button>)}</div>
          {view !== 'tasks' && <input className="inp" placeholder="Search sender, subject…" style={{ maxWidth: 240 }} value={q} onChange={e => setQ(e.target.value)} />}
        </div>
        {view === 'tasks'
          ? <TaskList tasks={tasks} onEdit={t => setTaskEdit({ task: t, draft: { ...t } })} onChanged={refresh} onOpenEmail={id => { setView('all'); setTimeout(() => open(id), 0); }} />
          : (
            <div className={`inbox-split${openId ? ' has-open' : ''}`}>
              <div className="inbox-list">
                {items === null ? <Empty><Spinner dark /> Loading…</Empty>
                  : items.length === 0 ? <Empty>{view === 'all' ? 'Nothing new. New Outlook mail appears here by itself.' : view === 'matches' ? 'No job emails with matching profiles yet. Click "Fetch matching profiles".' : view === 'awaiting' ? 'Everything you sent has an answer.' : view === 'sent' ? 'No sent mail from the last few days yet.' : 'No emails here.'}</Empty>
                  : items.map(i => (
                    <button key={i.id} className={`inbox-row${i.id === openId ? ' on' : ''}${i.status !== 'New' ? ' read' : ''}`} onClick={() => open(i.id)}>
                      <i className={`pdot ${i.direction === 'Out' ? (i.answeredUtc ? 'g' : 'y') : PRI[i.priority]}`} title={i.direction === 'Out' ? (i.answeredUtc ? 'Replied' : 'No reply yet') : `${i.priority} priority`} />
                      <div className="ir-main">
                        <div className="ir-top"><b>{i.direction === 'Out' ? `To: ${i.toAddresses || '—'}` : i.fromName || i.fromAddress}</b><small>{ago(i.receivedUtc)}</small></div>
                        <div className="ir-sub">{i.subject}</div>
                        <div className="ir-sum">{i.summary}</div>
                        {i.direction === 'Out' ? <div>{i.answeredUtc ? <span className="tag g">Replied {ago(i.answeredUtc)}</span>
                          : <span className={`tag ${daysSince(i.receivedUtc) >= 2 ? 'r' : 'y'}`}>No reply · {daysSince(i.receivedUtc)}d</span>}{i.attachments?.length ? i.attachments.slice(0, 2).map(a => <span key={a.index} className="tag s" title={a.name}>📎 {a.name.length > 24 ? a.name.slice(0, 22) + '…' : a.name}</span>) : i.hasAttachments && <span className="tag s">📎</span>}</div> : <div>
                          <span className={`tag ${CAT[i.category][1]}`}>{CAT[i.category][0]}</span>
                          {i.priority === 'High' && <span className="tag r">High</span>}
                          {i.matches.length > 0 && <span className="tag g">{i.matches.length} match{i.matches.length === 1 ? '' : 'es'}</span>}
                          {view === 'matches' && i.matches.slice(0, 3).map(m => <span key={m.consultantId} className="tag v">{m.name} {m.score}%</span>)}
                          {i.matches.some(m => m.resumeReady) && <span className="tag c">Resume ready</span>}
                          {i.needsReply && i.status === 'New' && <span className="tag y">Reply</span>}
                          {i.openTasks > 0 && <span className="tag s">{i.openTasks} task{i.openTasks === 1 ? '' : 's'}</span>}
                          {i.answeredUtc && <span className="tag g">You replied</span>}
                        </div>}
                      </div>
                    </button>))}
              </div>
              <div className="inbox-detail">
                {openId ? <EmailDetail key={openId} id={openId} canTailor={can('parser.use')} onClose={() => open(null)} onChanged={refresh}
                  onTask={d => setTaskEdit({ draft: d })} onEditTask={t => setTaskEdit({ task: t, draft: { ...t } })} />
                  : <Empty>Pick an email to see the AI summary, matches and next steps.</Empty>}
              </div>
            </div>)}
      </div>

      {taskEdit && <TaskDialog task={taskEdit.task} draft={taskEdit.draft} onClose={() => setTaskEdit(null)} onSaved={() => { setTaskEdit(null); refresh(); }} />}
      {alerts && <AlertsDialog onClose={() => setAlerts(false)} />}
    </>
  );
}

// =====================================================================================================

function EmailDetail({ id, canTailor, onClose, onChanged, onTask, onEditTask }: {
  id: number; canTailor: boolean; onClose: () => void; onChanged: () => void; onTask: (d: Partial<InboxTask>) => void; onEditTask: (t: InboxTask) => void;
}) {
  const onError = useErrorHandler();
  const notify = useToast();
  const nav = useNavigate();
  const [d, setD] = useState<InboxDetail | null>(null);
  const [drafting, setDrafting] = useState<number | 'reply' | null>(null);
  const [compose, setCompose] = useState<{ initial: ComposeInitial; replyId?: number; files?: File[] } | null>(null);
  const [showBody, setShowBody] = useState(false);

  const load = useCallback(() => { api<InboxDetail>(`/inbox/${id}`).then(setD).catch(e => { onError(e); onClose(); }); }, [id, onError, onClose]);
  useEffect(load, [load]);
  // Tasks edited in the dialog: reload this email's task list too.
  useEffect(() => { window.addEventListener('cbs:inbox', load); return () => window.removeEventListener('cbs:inbox', load); }, [load]);

  if (!d) return <Empty><Spinner dark /> Loading…</Empty>;
  const i = d.item;
  if (i.direction === 'Out') return <SentDetail d={d} onClose={onClose} onChanged={() => { onChanged(); load(); }} />;

  const setStatus = async (status: InboxItem['status']) => {
    try { await api(`/inbox/${id}`, { method: 'PATCH', body: { status } }); notify('success', status === 'New' ? 'Moved back to the inbox' : status === 'Done' ? 'Marked done' : 'Dismissed'); onChanged(); if (status !== 'New') onClose(); else load(); }
    catch (e) { onError(e); }
  };
  const draft = async (consultantId?: number) => {
    setDrafting(consultantId ?? 'reply');
    try {
      const r = await api<InboxDraft>(`/inbox/${id}/draft`, { method: 'POST', body: { consultantId } });
      if (r.notice) notify('info', 'Built-in engine wrote the draft', r.notice);
      // Pitching a consultant: attach their latest resume (the same file submissions use).
      const files: File[] = [];
      if (consultantId) {
        const m = i.matches.find(x => x.consultantId === consultantId);
        try { const f = await fetchFile(`/resumes/submission/${consultantId}`); files.push(new File([f.blob], f.name ?? `${m?.name ?? 'Consultant'} - Resume.docx`, { type: f.blob.type })); }
        catch (e) { notify('warning', 'Resume not attached', e instanceof ApiError ? e.detail : "The resume couldn't be loaded. Attach it in the email."); }
      }
      setCompose({ initial: { to: r.to, subject: r.subject, body: r.body }, replyId: r.replyToInboxItemId ?? undefined, files });
    } catch (e) { onError(e); } finally { setDrafting(null); }
  };
  const tailor = (m: InboxMatch) => {
    const j = i.job;
    const jd = `${j?.title ?? i.subject}${j?.client ? `\nClient: ${j.client}` : ''}${j?.location ? `\nLocation: ${j.location} (${j.mode})` : ''}` +
      `${j?.skills.length ? `\nSkills: ${j.skills.join(', ')}` : ''}${j?.minYears ? `\nExperience: ${j.minYears}+ years` : ''}\n\n${d.body}`;
    try { sessionStorage.setItem('cbs.tailorJob', JSON.stringify({ jobTitle: j?.title ?? i.subject, client: j?.client ?? '', jd: jd.slice(0, 12000) })); } catch { /* ignore */ }
    nav(`/resume-builder?consultant=${m.consultantId}&from=inbox&auto=1`);
  };
  const doneTask = async (t: InboxTask) => {
    try { await api(`/inbox/tasks/${t.id}`, { method: 'PUT', body: { status: t.status === 'Open' ? 'Done' : 'Open' } }); load(); onChanged(); } catch (e) { onError(e); }
  };

  return (
    <div className="inbox-mail">
      <div className="im-h">
        <button className="btn btn-sm btn-outline im-back" onClick={onClose}>← Back</button>
        <h3>{i.subject}</h3>
        <div className="muted" style={{ fontSize: 13 }}>{i.fromName ? <><b style={{ color: 'var(--ink)' }}>{i.fromName}</b> &lt;{i.fromAddress}&gt;</> : i.fromAddress} · {when(i.receivedUtc)}{i.hasAttachments && ' · 📎'}</div>
        <div style={{ marginTop: 6 }}>
          <span className={`tag ${PRI[i.priority]}`}>{i.priority} priority</span><span className={`tag ${CAT[i.category][1]}`}>{CAT[i.category][0]}</span>
          <AiBadge source={i.engine} />{i.status !== 'New' && <span className="tag s">{i.status}</span>}
        </div>
      </div>

      <div className="im-ai">
        <b>✦ AI summary</b>
        <p>{i.summary || 'No summary.'}</p>
        {i.priorityReason && <p className="muted" style={{ fontSize: 12.5 }}>Why {i.priority.toLowerCase()}: {i.priorityReason}</p>}
        {!!i.requests?.length && <p style={{ fontSize: 13 }}><b>Asks for:</b> {i.requests.map(r => <span key={r} className="tag y">{r}</span>)}</p>}
        {i.team && <p style={{ fontSize: 13 }}><b>For the {i.team} team</b>: forward it (see Tasks below).</p>}
        {!!i.slots?.length && <p style={{ fontSize: 13 }}><b>Interview slots:</b> {i.slots.map(s => <span key={s} className="tag g">{when(s)}</span>)}</p>}
      </div>

      <div className="im-act">
        <button className="btn btn-primary btn-sm" onClick={() => draft()} disabled={drafting !== null}>{drafting === 'reply' ? <><Spinner /> Drafting…</> : '✦ Draft follow-up reply'}</button>
        <button className="btn btn-outline btn-sm" onClick={() => onTask({ kind: 'Interview', title: `Interview: ${i.job?.title ?? i.subject}`, inboxItemId: i.id, durationMinutes: 30 })}>📅 Schedule interview</button>
        <button className="btn btn-outline btn-sm" onClick={() => onTask({ kind: 'FollowUp', title: `Follow up with ${i.fromName ?? i.fromAddress}`, inboxItemId: i.id, dueUtc: new Date(Date.now() + 86_400_000).toISOString() })}>+ Task</button>
        {i.status === 'New'
          ? <><button className="btn btn-outline btn-sm" onClick={() => setStatus('Done')}>✓ Done</button><button className="btn btn-outline btn-sm" onClick={() => setStatus('Dismissed')}>Dismiss</button></>
          : <button className="btn btn-outline btn-sm" onClick={() => setStatus('New')}>Move to inbox</button>}
      </div>

      {i.job && (
        <div className="im-sec">
          <h4>Job in this email</h4>
          <div className="im-job"><b>{i.job.title}</b>{i.job.client && <> · {i.job.client}</>}
            <div className="muted" style={{ fontSize: 13 }}>{[i.job.location, i.job.mode, i.job.type, i.job.rate ? `$${i.job.rate}/hr` : 'rate not given', i.job.minYears ? `${i.job.minYears}+ yrs` : null, i.job.visas.length ? i.job.visas.join('/') : null].filter(Boolean).join(' · ')}</div>
            <div style={{ marginTop: 4 }}>{i.job.skills.map(s => <span key={s} className="tag">{s}</span>)}</div></div>
          <h4 style={{ marginTop: 14 }}>Your consultants who fit</h4>
          {i.matches.length === 0 ? <p className="muted" style={{ fontSize: 13 }}>None of your available consultants match this job closely.</p>
            : i.matches.map(m => (
              <div key={m.consultantId} className="im-match">
                <Ring score={m.score} size={40} />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <b>{m.name}</b> <small className="muted">{m.title} · {m.exp} yrs</small>{m.resumeReady && <span className="tag c" title="Strong fit with enough experience: ready for the resume helper">Resume ready</span>}
                  <div className="muted" style={{ fontSize: 12.5 }}>{m.why}</div>
                </div>
                <div className="im-mact">
                  <button className="btn btn-sm btn-outline" title="Their latest resume" onClick={() => download(`/resumes/submission/${m.consultantId}`, {}, `${m.name} - Resume.docx`).catch(onError)}>📄 Resume</button>
                  <button className="btn btn-sm btn-outline" onClick={() => draft(m.consultantId)} disabled={drafting !== null}>{drafting === m.consultantId ? <Spinner dark /> : 'Pitch'}</button>
                  {canTailor && <button className={`btn btn-sm ${m.resumeReady ? 'btn-primary' : 'btn-outline'}`} onClick={() => tailor(m)}>✦ Tailor resume</button>}
                </div>
              </div>))}
        </div>)}

      <div className="im-sec">
        <h4>Tasks from this email</h4>
        {d.tasks.length === 0 ? <p className="muted" style={{ fontSize: 13 }}>No tasks. Add one, or schedule an interview.</p>
          : d.tasks.map(t => <TaskRow key={t.id} t={t} onToggle={() => doneTask(t)} onEdit={() => onEditTask(t)} onDone={load} />)}
      </div>

      <Attachments item={i} />

      <div className="im-sec">
        <button className="linkish" onClick={() => setShowBody(s => !s)}>{showBody ? '▾ Hide the email' : '▸ Show the email'}</button>
        {showBody && <div className="jd" style={{ marginTop: 8 }}>{d.body || '(empty)'}</div>}
      </div>

      {compose && (
        <ComposeDialog title="Reply" initial={compose.initial} initialFiles={compose.files} replyToInboxItemId={compose.replyId} onClose={() => setCompose(null)}
          onSent={() => { setCompose(null); notify('success', 'Reply sent', 'Sent in the same Outlook thread. The email is marked done.'); onChanged(); load(); }} />)}
    </div>
  );
}

/** Something you sent: who got it, whether they answered, and a one-click follow-up nudge in the same thread. */
function SentDetail({ d, onClose, onChanged }: { d: InboxDetail; onClose: () => void; onChanged: () => void }) {
  const onError = useErrorHandler();
  const notify = useToast();
  const [busy, setBusy] = useState(false);
  const [compose, setCompose] = useState<{ initial: ComposeInitial; replyId?: number; files: File[] } | null>(null);
  const i = d.item;
  const days = daysSince(i.receivedUtc);
  const nudge = async () => {
    setBusy(true);
    try {
      const r = await api<InboxDraft>(`/inbox/${i.id}/draft`, { method: 'POST', body: {} });
      if (r.notice) notify('info', 'Built-in engine wrote the draft', r.notice);
      // Outlook doesn't carry attachments into a reply, so attach the original files again (e.g. the resume). Remove any you don't want.
      const files: File[] = [];
      for (const a of (i.attachments ?? []).slice(0, 5)) {
        try { const f = await fetchFile(`/inbox/${i.id}/attachments/${a.index}`); files.push(new File([f.blob], f.name ?? a.name, { type: a.contentType ?? f.blob.type })); }
        catch { /* the draft still opens without it */ }
      }
      setCompose({ initial: { to: r.to, subject: r.subject, body: r.body }, replyId: r.replyToInboxItemId ?? undefined, files });
    } catch (e) { onError(e); } finally { setBusy(false); }
  };
  return (
    <div className="inbox-mail">
      <div className="im-h">
        <button className="btn btn-sm btn-outline im-back" onClick={onClose}>← Back</button>
        <h3>{i.subject}</h3>
        <div className="muted" style={{ fontSize: 13 }}>You sent this to <b style={{ color: 'var(--ink)' }}>{i.toAddresses || '—'}</b> · {when(i.receivedUtc)}{i.hasAttachments && ' · 📎'}</div>
        <div style={{ marginTop: 6 }}><span className="tag s">Sent</span>
          {i.answeredUtc ? <span className="tag g">Replied {ago(i.answeredUtc)}</span> : <span className={`tag ${days >= 2 ? 'r' : 'y'}`}>No reply yet · {days} day{days === 1 ? '' : 's'}</span>}</div>
      </div>
      <div className="im-ai">
        <b>{i.answeredUtc ? '✓ They answered' : '⏳ Waiting for an answer'}</b>
        <p>{i.answeredUtc ? 'The reply is in your Inbox view, in the same thread.' : days >= 2 ? `No reply in ${days} days. A short follow-up usually gets things moving.` : 'Give it a day; a follow-up is offered after that.'}</p>
      </div>
      <div className="im-act">
        <button className={`btn btn-sm ${!i.answeredUtc && days >= 1 ? 'btn-primary' : 'btn-outline'}`} onClick={nudge} disabled={busy}>{busy ? <><Spinner /> Drafting…</> : '✦ Draft follow-up'}</button>
      </div>
      <Attachments item={i} />
      <div className="im-sec"><h4>What you sent</h4><div className="jd">{d.body || '(empty)'}</div></div>
      {compose && (
        <ComposeDialog title="Follow up" initial={compose.initial} initialFiles={compose.files} replyToInboxItemId={compose.replyId} onClose={() => setCompose(null)}
          onSent={() => { setCompose(null); notify('success', 'Follow-up sent', 'Sent in the same Outlook thread.'); onChanged(); }} />)}
    </div>
  );
}

const kb = (n: number) => (n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);
const isResume = (a: InboxAttachment) => /\.(pdf|docx?|rtf)$/i.test(a.name) && !/^(image\d*|logo)/i.test(a.name);

/** The email's files (resumes and so on), downloaded straight from Outlook. */
function Attachments({ item }: { item: InboxItem }) {
  const onError = useErrorHandler();
  const [busy, setBusy] = useState<number | null>(null);
  const list = item.attachments ?? [];
  if (!item.hasAttachments && list.length === 0) return null;
  const get = async (a: InboxAttachment) => {
    setBusy(a.index);
    try { await download(`/inbox/${item.id}/attachments/${a.index}`, {}, a.name); } catch (e) { onError(e); } finally { setBusy(null); }
  };
  return (
    <div className="im-sec">
      <h4>Attachments{list.length ? ` (${list.length})` : ''}</h4>
      {list.length === 0 ? <p className="muted" style={{ fontSize: 13 }}>This email has attachments, but Outlook couldn't list them right now. Open the email again in a minute.</p>
        : <div className="att-list">{list.map(a => (
          <button key={a.index} className="att" onClick={() => get(a)} disabled={busy !== null} title={`Download ${a.name}`}>
            <span className="att-ic">{isResume(a) ? '📄' : '📎'}</span>
            <span className="att-n"><b>{a.name}</b><small>{kb(a.size)}{isResume(a) ? ' · resume' : ''}</small></span>
            {busy === a.index ? <Spinner dark /> : <span className="att-dl">↓</span>}
          </button>))}</div>}
    </div>
  );
}

export function TaskRow({ t, onToggle, onEdit, onOpenEmail, onDone, showOwner }: { t: InboxTask; onToggle: () => void; onEdit: () => void; onOpenEmail?: () => void; onDone?: () => void; showOwner?: boolean }) {
  return (
    <div className={`task-row${t.status !== 'Open' ? ' closed' : ''}`}>
      <input type="checkbox" checked={t.status !== 'Open'} onChange={onToggle} aria-label={t.status === 'Open' ? 'Mark done' : 'Reopen'} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div><span className={`tag ${t.kind === 'Interview' ? 'g' : t.kind === 'FollowUp' ? 'y' : 's'}`}>{KIND[t.kind]}</span>
          {t.interviewType && <span className="tag c">{t.interviewType}</span>}{t.mockSession && <span className="tag v" title="Mock session planned before the interview">Mock</span>}
          <b>{t.title}</b>{t.auto && <span className="tag s" title="Created by AI from the email">AI</span>}</div>
        <div className="muted" style={{ fontSize: 12.5 }}>
          <span style={overdue(t) ? { color: 'var(--bad)', fontWeight: 600 } : undefined}>{when(t.dueUtc)}{t.durationMinutes ? ` · ${t.durationMinutes} min` : ''}</span>
          {showOwner && t.ownerName && <> · {t.ownerName}</>}{t.consultantName && <> · {t.consultantName}</>}{t.location && <> · {t.location}</>}
          {!t.dueUtc && !!t.slots?.length && <> · slots: {t.slots.map(when).join(' / ')}</>}
          {t.kind === 'Interview' && (t.sharedUtc ? <> · <span style={{ color: 'var(--ok)' }}>sent to consultant {ago(t.sharedUtc)}</span></> : <> · not shared yet</>)}
          {onOpenEmail && t.inboxItemId && <> · <button className="linkish" onClick={onOpenEmail}>{t.emailSubject ?? 'email'}</button></>}
        </div>
      </div>
      <div className="task-act">
        <TaskDraftButton t={t} onDone={onDone} />
        <button className="btn btn-sm btn-outline" onClick={onEdit}>{t.kind === 'Interview' ? 'Edit / share' : 'Edit'}</button>
      </div>
    </div>
  );
}

function TaskList({ tasks, onEdit, onChanged, onOpenEmail }: { tasks: InboxTask[] | null; onEdit: (t: InboxTask) => void; onChanged: () => void; onOpenEmail: (id: number) => void }) {
  const onError = useErrorHandler();
  if (tasks === null) return <Empty><Spinner dark /> Loading…</Empty>;
  if (tasks.length === 0) return <Empty>No open tasks. AI adds them from your emails; you can add your own too.</Empty>;
  const toggle = async (t: InboxTask) => { try { await api(`/inbox/tasks/${t.id}`, { method: 'PUT', body: { status: 'Done' } }); onChanged(); } catch (e) { onError(e); } };
  return <div className="card-b">{tasks.map(t => <TaskRow key={t.id} t={t} onToggle={() => toggle(t)} onEdit={() => onEdit(t)} onDone={onChanged} onOpenEmail={t.inboxItemId ? () => onOpenEmail(t.inboxItemId!) : undefined} />)}</div>;
}

function TaskDialog({ task, draft, onClose, onSaved }: { task?: InboxTask; draft: Partial<InboxTask>; onClose: () => void; onSaved: () => void }) {
  const { scope } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const [f, setF] = useState({
    kind: draft.kind ?? 'Task', title: draft.title ?? '', due: toLocal(draft.dueUtc), duration: draft.durationMinutes ? String(draft.durationMinutes) : '',
    consultantId: draft.consultantId ? String(draft.consultantId) : '', location: draft.location ?? '', notes: draft.notes ?? '', share: false,
    interviewType: draft.interviewType ?? '', mock: !!draft.mockSession,
  });
  const [cons, setCons] = useState<Consultant[]>([]);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  useEffect(() => { api<Consultant[]>('/consultants', { query: { scope } }).then(setCons).catch(() => setCons([])); }, [scope]);
  const interview = f.kind === 'Interview';
  const chosen = useMemo(() => cons.find(c => String(c.id) === f.consultantId), [cons, f.consultantId]);

  const save = async (andShare: boolean) => {
    setBusy(true); setErrors({});
    try {
      const body = {
        inboxItemId: draft.inboxItemId ?? undefined, kind: f.kind, title: f.title, dueUtc: fromLocal(f.due), durationMinutes: f.duration ? Number(f.duration) : undefined,
        consultantId: f.consultantId ? Number(f.consultantId) : undefined, location: f.location, notes: f.notes,
        interviewType: f.kind === 'Interview' ? f.interviewType : undefined, mockSession: f.kind === 'Interview' ? f.mock : undefined,
      };
      const saved = task ? await api<InboxTask>(`/inbox/tasks/${task.id}`, { method: 'PUT', body }) : await api<InboxTask>('/inbox/tasks', { method: 'POST', body });
      if (andShare) {
        await api(`/inbox/tasks/${saved.id}/share`, { method: 'POST', body: {} });
        notify('success', 'Interview shared', `${chosen?.name ?? 'The consultant'} got the details and a calendar invite from your Outlook.`);
      } else notify('success', task ? 'Task updated' : 'Task added', f.title);
      onSaved();
    } catch (e) {
      onError(e, setErrors);
      if (e instanceof ApiError && andShare && e.status !== 400) onSaved();
    } finally { setBusy(false); }
  };
  const close = async () => {
    if (!task) return;
    try { await api(`/inbox/tasks/${task.id}`, { method: 'PUT', body: { status: 'Dismissed' } }); notify('info', 'Task dismissed'); onSaved(); } catch (e) { onError(e); }
  };

  return (
    <Modal title={task ? (interview ? 'Interview' : 'Edit task') : interview ? 'Schedule interview' : 'New task'} onClose={onClose}
      footer={<>{task && task.status === 'Open' && <button className="btn btn-outline" style={{ marginRight: 'auto' }} onClick={close}>Dismiss</button>}
        <button className="btn btn-outline" onClick={onClose}>Cancel</button>
        {interview && <button className="btn btn-outline" onClick={() => save(true)} disabled={busy || !f.consultantId || !f.due} title={!f.consultantId || !f.due ? 'Pick the consultant and the time first' : undefined}>
          {task?.sharedUtc ? 'Save & send again' : 'Save & send to consultant'}</button>}
        <button className="btn btn-primary" onClick={() => save(false)} disabled={busy}>{busy ? <Spinner /> : 'Save'}</button></>}>
      <div className="row2">
        <Field label="Type" error={errors.kind}><select className="inp" value={f.kind} onChange={e => setF({ ...f, kind: e.target.value as InboxTask['kind'] })}>
          <option value="Task">Task</option><option value="FollowUp">Follow-up</option><option value="Interview">Interview</option></select></Field>
        <Field label={interview ? 'Date & time *' : 'Due'} error={errors.dueUtc}><input className="inp" type="datetime-local" value={f.due} onChange={e => setF({ ...f, due: e.target.value })} /></Field>
      </div>
      <Field label="Title *" error={errors.title}><input className="inp" value={f.title} onChange={e => setF({ ...f, title: e.target.value })} maxLength={300} /></Field>
      <div className="row2">
        <Field label={interview ? 'Consultant *' : 'Consultant'} error={errors.consultantId}><select className="inp" value={f.consultantId} onChange={e => setF({ ...f, consultantId: e.target.value })}>
          <option value="">{cons.length ? 'Choose…' : 'No consultants in your view'}</option>{cons.map(c => <option key={c.id} value={c.id}>{c.name} · {c.title}</option>)}</select></Field>
        {interview && <Field label="Duration (minutes)" error={errors.durationMinutes}><input className="inp" type="number" min={5} max={480} value={f.duration} onChange={e => setF({ ...f, duration: e.target.value })} placeholder="30" /></Field>}
      </div>
      {interview && !!draft.slots?.length && (
        <Field label="Slots the vendor offered (pick the one the consultant chose)">
          <div>{draft.slots.map(s => <button key={s} type="button" className={`tag ${toLocal(s) === f.due ? 'g' : 's'}`} style={{ cursor: 'pointer', border: 0 }}
            onClick={() => setF({ ...f, due: toLocal(s) })}>{when(s)}</button>)}</div>
        </Field>)}
      {interview && <div className="row2">
        <Field label="Interview type" error={errors.interviewType}><select className="inp" value={f.interviewType} onChange={e => setF({ ...f, interviewType: e.target.value })}>
          <option value="">—</option>{INTERVIEW_TYPES.map(t => <option key={t}>{t}</option>)}</select></Field>
        <Field label="Practice"><label className="check" style={{ border: 0, padding: '8px 0' }}><input type="checkbox" checked={f.mock} onChange={e => setF({ ...f, mock: e.target.checked })} />
          <span>Plan a mock session first (no interview practice yet)</span></label></Field>
      </div>}
      {interview && <p className="muted" style={{ fontSize: 12, margin: '-4px 0 8px' }}>Only the consultant attends the interview; the app records nobody else as taking part.</p>}
      {interview && <Field label="Meeting link, phone or address" error={errors.location}><input className="inp" value={f.location} onChange={e => setF({ ...f, location: e.target.value })} placeholder="https://teams.microsoft.com/… or +1 555…" /></Field>}
      <Field label="Notes" error={errors.notes}><textarea className="inp" style={{ minHeight: 70 }} value={f.notes} onChange={e => setF({ ...f, notes: e.target.value })} placeholder={interview ? 'Interviewer, round, what to prepare…' : ''} /></Field>
      {interview && chosen && !chosen.email && <Alert type="warning" title="No email on file">Add {chosen.name}'s email on the bench to send them the interview details.</Alert>}
      {interview && task?.sharedUtc && <p className="muted" style={{ fontSize: 12.5 }}>Sent to the consultant {ago(task.sharedUtc)}. Changed the time? Send it again.</p>}
    </Modal>
  );
}

function AlertsDialog({ onClose }: { onClose: () => void }) {
  const notify = useToast();
  const [p, setP] = useState<InboxAlertPrefs>(loadAlertPrefs);
  const [perm, setPerm] = useState(typeof Notification === 'undefined' ? 'unsupported' : Notification.permission);
  const ask = async () => { if (typeof Notification === 'undefined') return; setPerm(await Notification.requestPermission()); };
  const save = () => { saveAlertPrefs(p); notify('success', 'Alert settings saved'); onClose(); };
  const test = () => {
    playChime();
    if (perm === 'granted') { try { new Notification('High priority: test alert', { body: 'This is how new high-priority emails will look.', icon: '/brand/collaborate-mark.png' }); } catch { /* ignore */ } }
    notify('warning', 'High priority: test alert', 'This is how new high-priority emails will look.');
  };
  return (
    <Modal title="High-priority alerts" onClose={onClose}
      footer={<><button className="btn btn-outline" style={{ marginRight: 'auto' }} onClick={test}>Test alert</button><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save}>Save</button></>}>
      <p className="muted" style={{ marginTop: 0 }}>When a new high-priority email arrives during your workday, you get a notification and a sound. Outside those hours the tile still turns red, quietly.</p>
      <label className="check"><input type="checkbox" checked={p.sound} onChange={e => setP({ ...p, sound: e.target.checked })} /><span>Play a sound</span></label>
      <label className="check"><input type="checkbox" checked={p.desktop} onChange={e => setP({ ...p, desktop: e.target.checked })} /><span>Desktop notification</span></label>
      {p.desktop && perm !== 'granted' && (
        <Alert type="info" title={perm === 'denied' ? 'Notifications are blocked for this site' : perm === 'unsupported' ? "This browser doesn't support notifications" : 'Allow notifications'}>
          {perm === 'default' ? <button className="btn btn-sm btn-primary" onClick={ask}>Allow desktop notifications</button>
            : perm === 'denied' ? 'Allow them in the browser’s site settings (the lock icon in the address bar).' : null}
        </Alert>)}
      <div className="row2">
        <Field label="Workday starts"><select className="inp" value={p.startHour} onChange={e => setP({ ...p, startHour: Number(e.target.value) })}>{Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{`${h}:00`}</option>)}</select></Field>
        <Field label="Workday ends"><select className="inp" value={p.endHour} onChange={e => setP({ ...p, endHour: Number(e.target.value) })}>{Array.from({ length: 24 }, (_, h) => <option key={h + 1} value={h + 1}>{`${h + 1}:00`}</option>)}</select></Field>
      </div>
      <p className="muted" style={{ fontSize: 12.5 }}>Monday to Friday, in this computer’s time zone. Right now: {inWorkday(p) ? 'inside your workday, alerts are on' : 'outside your workday, alerts are quiet'}.</p>
    </Modal>
  );
}
