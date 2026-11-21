import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { AlertPrefs, api, AutopilotItem, AutopilotSettings, AutopilotSummary, VendorEmail, VendorReply } from '../api';
import { useAuth } from '../auth';
import { ComposeDialog } from '../compose';
import { Alert, Empty, Field, FieldErrors, Modal, Ring, Spinner, ago, money, useConfirm, useErrorHandler, useToast } from '../ui';

type Tab = 'approval' | 'rtr' | 'submitted' | 'replies' | 'attention' | 'skipped';
const INTENT: Record<string, [string, string]> = {
  interview: ['g', 'Interview request'], rejected: ['r', 'Rejected'], closed: ['s', 'Position closed'], needs_info: ['y', 'Needs info'], rate: ['c', 'Rate question'], other: ['s', 'Other'],
};

/** Notifies the sidebar badge that Autopilot counts changed. */
const changed = () => window.dispatchEvent(new Event('cbs:autopilot'));

export default function Autopilot() {
  const { can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const [summary, setSummary] = useState<AutopilotSummary | null>(null);
  const [settings, setSettings] = useState<AutopilotSettings | null>(null);
  // ?tab= opens a specific tab (links from the dashboard card).
  const [tab, setTab] = useState<Tab>(() => {
    const t = new URLSearchParams(window.location.search).get('tab');
    return t === 'rtr' || t === 'submitted' || t === 'replies' || t === 'attention' || t === 'skipped' ? t : 'approval';
  });
  const [items, setItems] = useState<AutopilotItem[] | null>(null);
  const [replies, setReplies] = useState<VendorReply[] | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const [showAlerts, setShowAlerts] = useState(false);
  const [running, setRunning] = useState(false);

  const loadSummary = useCallback(() => {
    api<AutopilotSummary>('/autopilot/summary').then(setSummary).catch(onError);
    api<AutopilotSettings>('/autopilot/settings').then(setSettings).catch(onError);
  }, [onError]);
  const loadTab = useCallback(() => {
    if (tab === 'replies') { setReplies(null); api<VendorReply[]>('/autopilot/replies').then(setReplies).catch(e => { setReplies([]); onError(e); }); }
    else { setItems(null); api<AutopilotItem[]>('/autopilot/items', { query: { group: tab } }).then(setItems).catch(e => { setItems([]); onError(e); }); }
  }, [tab, onError]);
  const refresh = useCallback(() => { loadSummary(); loadTab(); changed(); }, [loadSummary, loadTab]);
  useEffect(loadSummary, [loadSummary]);
  useEffect(loadTab, [loadTab]);

  const runNow = async () => {
    setRunning(true);
    try {
      await api('/autopilot/run', { method: 'POST' });
      notify('info', 'Autopilot is running', 'Finding and preparing matches. New submissions appear here in a minute or two.');
      setTimeout(refresh, 15000); setTimeout(refresh, 45000);
    } catch (e) { onError(e); }
    finally { setRunning(false); }
  };

  const tabs: [Tab, string, number?][] = [
    ['approval', 'Approve', summary?.awaitingApproval], ['rtr', 'Waiting on RTR', summary?.awaitingRtr], ['submitted', 'Submitted'],
    ['replies', 'Vendor replies', summary?.repliesToReview], ['attention', 'Needs attention', (summary?.needsResume ?? 0) + (summary?.failed ?? 0)], ['skipped', 'Skipped'],
  ];

  return (
    <>
      <div className="ph">
        <div><h1>Autopilot</h1>
          <p>The AI finds matches for your bench, tailors resumes and drafts submissions. You approve, the consultant e-signs the RTR, and it's sent to the vendor and followed up automatically.</p></div>
        <div className="ph-act">
          <button className="btn btn-outline" onClick={() => setShowAlerts(true)}>🔔 Email alerts</button>
          {settings?.canManage && <button className="btn btn-outline" onClick={() => setShowSettings(true)}>Settings</button>}
          {settings?.canManage && <button className="btn btn-dark" onClick={runNow} disabled={running || !summary?.enabled}>{running ? <Spinner /> : '⚡ Run now'}</button>}
        </div>
      </div>

      {summary && (
        <div className={`ap-status ${summary.enabled ? 'on' : 'off'}`}>
          <span className="ap-dot" /><b>{summary.enabled ? 'Autopilot is on' : 'Autopilot is off'}</b>
          <span className="muted">{summary.lastRunUtc ? `Last run ${ago(summary.lastRunUtc)}: ${summary.lastRunSummary ?? ''}` : 'Not run yet.'}</span>
          {!summary.enabled && <span className="muted">{settings?.canManage ? 'Turn it on in Settings to start finding matches.' : 'Ask your Team Lead to turn it on.'}</span>}
        </div>)}
      {settings && !settings.aiConfigured && <Alert type="info" title="Running with the built-in engine">No Anthropic API key is set, so resumes are tailored in summary and skills only and replies are read with keyword rules. Add Anthropic:ApiKey for full AI.</Alert>}

      <div className="ap-funnel">
        {[['Found', summary?.found], ['Approve', summary?.awaitingApproval], ['Waiting on RTR', summary?.awaitingRtr], ['Submitted (7 days)', summary?.submittedThisWeek], ['Vendor replies', summary?.repliesToReview]].map(([l, n], i) => (
          <div key={String(l)} className="ap-stage"><small>{String(i + 1).padStart(2, '0')}</small><b>{n ?? '…'}</b><span>{l}</span></div>))}
      </div>

      <div className="card">
        <div className="card-h"><div className="tabs">{tabs.map(([k, l, n]) => <button key={k} className={tab === k ? 'on' : ''} onClick={() => setTab(k)}>{l}{n ? ` (${n})` : ''}</button>)}</div></div>
        <div className="card-b">
          {tab === 'replies' ? <Replies replies={replies} onChanged={refresh} />
            : items === null ? <Empty><Spinner dark /> Loading…</Empty>
            : items.length === 0 ? <Empty>{EMPTY[tab]}</Empty>
            : tab === 'approval' ? items.map(i => <ApprovalCard key={i.id} item={i} canApprove={can('sub.create')} onDone={refresh} />)
            : <ItemTable tab={tab} items={items} onChanged={refresh} />}
        </div>
      </div>
      {showAlerts && <AlertsDialog onClose={() => setShowAlerts(false)} />}
      {showSettings && settings && <SettingsDialog settings={settings} onClose={() => setShowSettings(false)} onSaved={s => { setSettings(s); setShowSettings(false); loadSummary(); }} />}
    </>
  );
}

const EMPTY: Record<Tab, string> = {
  approval: 'Nothing waiting for approval. The Autopilot adds strong matches here as it finds them.',
  rtr: 'No RTRs waiting on consultants.', submitted: 'Nothing submitted by the Autopilot yet.', replies: 'No vendor replies to review.',
  attention: 'Nothing needs attention.', skipped: 'Nothing skipped.',
};

function ApprovalCard({ item, canApprove, onDone }: { item: AutopilotItem; canApprove: boolean; onDone: () => void }) {
  const notify = useToast();
  const onError = useErrorHandler();
  const confirm = useConfirm();
  const external = !item.requirementId;
  const [f, setF] = useState({
    vendorEmail: item.vendorEmail ?? '', emailSubject: item.emailSubject ?? '', emailBody: item.emailBody ?? '',
    payRate: item.payRate ?? '', submitRate: item.submitRate ?? '', billRate: item.billRate ?? '', contractType: item.contractType ?? 'C2C',
  });
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState(false);
  const num = (v: string) => (v === '' ? '' : Number(v));
  const margin = Number(f.submitRate) - Number(f.payRate);

  const approve = async () => {
    setBusy(true); setErrors({});
    try {
      await api(`/autopilot/items/${item.id}/approve`, { method: 'POST', body: { ...f, payRate: Number(f.payRate) || null, submitRate: Number(f.submitRate) || null, billRate: Number(f.billRate) || null } });
      notify('success', 'RTR sent to the consultant', `${item.consultantName} will get an email to approve. Once they do, it's sent to the vendor automatically.`);
      onDone();
    } catch (e) { onError(e, setErrors); setOpen(true); }
    finally { setBusy(false); }
  };
  const skip = async () => {
    if (!await confirm({ title: 'Skip this match?', message: `${item.consultantName} won't be suggested for ${item.jobTitle} again.`, okLabel: 'Skip' })) return;
    try { await api(`/autopilot/items/${item.id}/skip`, { method: 'POST' }); notify('success', 'Skipped'); onDone(); } catch (e) { onError(e); }
  };

  return (
    <div className="ap-card">
      <div className="ap-card-h">
        <Ring score={item.score} size={52} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <b>{item.consultantName}</b> <span className="muted">→</span> <b>{item.jobTitle}</b>
          <div className="muted" style={{ fontSize: 13 }}>{[item.client, item.vendor && item.vendor !== 'Direct client' ? `via ${item.vendor}` : null, external ? 'External job' : null, item.ownerName].filter(Boolean).join(' · ')}</div>
          {item.why && <div className="why">{item.why}</div>}
          <div style={{ marginTop: 6 }}>
            {item.gaps.map(g => <span key={g} className="tag r" title="In the job, not in the resume; not added">{g}</span>)}
            {item.status !== 'Prepared' && <span className="tag y">{item.statusLabel}</span>}
          </div>
        </div>
        <div className="ap-rates">
          <span>Pay <b>{item.payRate ? money(item.payRate) : '–'}</b></span><span>Submit <b>{item.submitRate ? money(item.submitRate) : '–'}</b></span><span>Bill <b>{item.billRate ? money(item.billRate) : '–'}</b></span>
        </div>
      </div>
      {item.error && <Alert type="warning" title={item.status === 'Prepared' ? 'Check before approving' : item.statusLabel}>{item.error}</Alert>}
      {item.resumeWarnings > 0 && !item.error && <Alert type="warning" title={`${item.resumeWarnings} item(s) in the tailored resume need checking`}>Open the resume and confirm them before approving.</Alert>}

      {open && <div className="ap-edit">
        <div className="row3">
          <Field label="Pay rate ($/hr) *" error={errors.payRate}><input className="inp" type="number" value={f.payRate} onChange={e => setF({ ...f, payRate: num(e.target.value) })} /></Field>
          <Field label="Submit rate ($/hr) *" error={errors.submitRate}><input className="inp" type="number" value={f.submitRate} onChange={e => setF({ ...f, submitRate: num(e.target.value) })} /></Field>
          <Field label={`Bill rate ($/hr)${external ? ' *' : ''}`} error={errors.billRate}><input className="inp" type="number" value={f.billRate} disabled={!external} onChange={e => setF({ ...f, billRate: num(e.target.value) })} /></Field>
        </div>
        {margin > 0 && <p className="muted" style={{ fontSize: 12.5, marginTop: -6 }}>Margin {money(margin)}/hr</p>}
        {external && <Field label="Contract type *" error={errors.contractType}><select className="inp" value={f.contractType} onChange={e => setF({ ...f, contractType: e.target.value })}>{['C2C', 'W2', '1099'].map(t => <option key={t}>{t}</option>)}</select></Field>}
        <Field label="Vendor contact email *" error={errors.vendorEmail}><input className="inp" value={f.vendorEmail} onChange={e => setF({ ...f, vendorEmail: e.target.value })} placeholder="recruiter@vendor.com" /></Field>
        <Field label="Subject *" error={errors.emailSubject}><input className="inp" value={f.emailSubject} onChange={e => setF({ ...f, emailSubject: e.target.value })} /></Field>
        <Field label="Email to the vendor *" error={errors.emailBody}><textarea className="inp" style={{ minHeight: 180 }} value={f.emailBody} onChange={e => setF({ ...f, emailBody: e.target.value })} /></Field>
        {errors.consultant && <Alert type="error" title="Consultant email missing">{errors.consultant.join(' ')}</Alert>}
      </div>}

      <div className="flex" style={{ justifyContent: 'flex-end', marginTop: 10 }}>
        {item.tailoredResumeId && <Link className="btn btn-sm btn-outline" to={`/resume-builder?consultant=${item.consultantId}`}>View tailored resume</Link>}
        <button className="btn btn-sm btn-outline" onClick={() => setOpen(o => !o)}>{open ? 'Hide details' : 'Review email & rates'}</button>
        {canApprove && <button className="btn btn-sm" style={{ color: 'var(--muted)' }} onClick={skip}>Skip</button>}
        {canApprove && <button className="btn btn-sm btn-primary" onClick={() => (open ? approve() : setOpen(true))} disabled={busy}>
          {busy ? <Spinner /> : open ? `Approve & send RTR to ${item.consultantName.split(' ')[0]}` : item.status === 'Prepared' ? 'Approve…' : 'Send RTR again…'}</button>}
      </div>
    </div>
  );
}

function ItemTable({ tab, items, onChanged }: { tab: Tab; items: AutopilotItem[]; onChanged: () => void }) {
  const onError = useErrorHandler();
  const notify = useToast();
  const retry = async (i: AutopilotItem) => {
    try { await api(`/autopilot/items/${i.id}/retry`, { method: 'POST' }); notify('success', i.status === 'Skipped' ? 'Restored' : 'Retrying', 'The Autopilot will pick it up shortly.'); onChanged(); }
    catch (e) { onError(e); }
  };
  return (
    <div className="tw"><table><thead><tr><th>Consultant → job</th><th>Match</th><th>{tab === 'submitted' ? 'Submitted' : tab === 'rtr' ? 'RTR' : 'Status'}</th><th>Recruiter</th><th /></tr></thead><tbody>
      {items.map(i => (
        <tr key={i.id}>
          <td><b>{i.consultantName}</b> → {i.jobTitle}<div className="muted" style={{ fontSize: 12 }}>{[i.client, i.vendor].filter(Boolean).join(' · ')}</div></td>
          <td><span className="tag g">{i.score}%</span></td>
          <td style={{ fontSize: 13 }}>
            {tab === 'submitted' ? <>{i.submittedUtc ? ago(i.submittedUtc) : ''}{i.approvedBy ? ` · approved by ${i.approvedBy}` : ''}{i.rtr?.signedName ? <div className="muted">RTR e-signed by {i.rtr.signedName}</div> : null}</>
              : tab === 'rtr' ? <>{i.status === 'RtrApproved' ? <span className="tag g">Approved, sending…</span> : <span className="tag y">Waiting</span>}
                  {i.rtr && <div className="muted">Sent {ago(i.rtr.createdUtc)} · expires {new Date(i.rtr.expiresUtc).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}</div>}</>
              : <><span className={`tag ${i.status === 'Failed' ? 'r' : i.status === 'Skipped' ? 's' : 'y'}`}>{i.statusLabel}</span>{i.error && <div className="muted">{i.error}</div>}</>}
          </td>
          <td>{i.ownerName}</td>
          <td style={{ whiteSpace: 'nowrap' }}>
            {i.status === 'NeedsResume' && <Link className="btn btn-sm btn-outline" to={`/resume-builder?consultant=${i.consultantId}`}>Upload resume</Link>}
            {(i.status === 'Failed' || i.status === 'Skipped') && <button className="btn btn-sm btn-outline" onClick={() => retry(i)}>{i.status === 'Skipped' ? 'Restore' : 'Retry'}</button>}
            {i.submissionId && <Link className="btn btn-sm btn-outline" to="/pipeline">Pipeline</Link>}
          </td>
        </tr>))}
    </tbody></table></div>
  );
}

function Replies({ replies, onChanged }: { replies: VendorReply[] | null; onChanged: () => void }) {
  const onError = useErrorHandler();
  const notify = useToast();
  const [reply, setReply] = useState<VendorReply | null>(null);
  const handled = (r: VendorReply) => api(`/autopilot/replies/${r.emailId}/handled`, { method: 'POST' });
  const act = async (r: VendorReply, action: 'interview' | 'withdraw' | 'dismiss') => {
    try {
      if (action === 'interview') { await api(`/submissions/${r.submissionId}/stage`, { method: 'POST', body: { toStage: 'Client Interview' } }); notify('success', 'Moved to Client Interview', `${r.consultantName} → ${r.jobTitle}`); }
      if (action === 'withdraw') { await api(`/submissions/${r.submissionId}`, { method: 'DELETE' }); notify('success', 'Submission withdrawn', `${r.consultantName} → ${r.jobTitle}`); }
      await handled(r); onChanged();
    } catch (e) { onError(e); }
  };
  if (replies === null) return <Empty><Spinner dark /> Loading…</Empty>;
  if (replies.length === 0) return <Empty>No vendor replies to review. The Autopilot reads new replies from connected Outlook mailboxes.</Empty>;
  return (
    <>
      {replies.map(r => {
        const [cls, label] = INTENT[r.intent] ?? INTENT.other;
        return (
          <div key={r.emailId} className="ap-reply">
            <div style={{ flex: 1, minWidth: 0 }}>
              <div className="flex" style={{ gap: 6 }}><span className={`tag ${cls}`}>{label}</span><b>{r.summary || r.subject}</b></div>
              <small className="muted">{r.fromName || r.fromAddress} · {r.vendor} · {ago(r.sentUtc)}{r.consultantName ? ` · ${r.consultantName}` : ''}{r.jobTitle ? ` → ${r.jobTitle}` : ''}{r.stage ? ` (${r.stage})` : ''}</small>
              <div className="why" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.preview}</div>
            </div>
            <div className="flex" style={{ flexWrap: 'nowrap' }}>
              {r.intent === 'interview' && r.submissionId && r.stage !== 'Client Interview' && <button className="btn btn-sm btn-primary" onClick={() => act(r, 'interview')}>Move to interview</button>}
              {(r.intent === 'rejected' || r.intent === 'closed') && r.submissionId && <button className="btn btn-sm btn-outline" onClick={() => act(r, 'withdraw')}>Withdraw</button>}
              <button className="btn btn-sm btn-outline" onClick={() => setReply(r)}>Reply</button>
              <button className="btn btn-sm" style={{ color: 'var(--muted)' }} onClick={() => act(r, 'dismiss')}>Done</button>
            </div>
          </div>);
      })}
      {reply && <ComposeDialog onClose={() => setReply(null)} onSent={() => { handled(reply).then(onChanged).catch(() => {}); }}
        replyTo={{ id: reply.emailId, direction: 'In', fromAddress: reply.fromAddress, toAddresses: '', subject: reply.subject } as VendorEmail} />}
    </>
  );
}

function SettingsDialog({ settings, onClose, onSaved }: { settings: AutopilotSettings; onClose: () => void; onSaved: (s: AutopilotSettings) => void }) {
  const onError = useErrorHandler();
  const notify = useToast();
  const [s, setS] = useState(settings);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const num = (k: keyof AutopilotSettings) => (e: React.ChangeEvent<HTMLInputElement>) => setS({ ...s, [k]: Number(e.target.value) });
  const save = async () => {
    setBusy(true); setErrors({});
    try { const r = await api<AutopilotSettings>('/autopilot/settings', { method: 'PUT', body: s }); notify('success', 'Autopilot settings saved', r.enabled ? 'It runs on schedule; first run starting now.' : 'The Autopilot is off.'); onSaved(r); }
    catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };
  return (
    <Modal title="Autopilot settings" onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save} disabled={busy}>{busy ? <Spinner /> : 'Save'}</button></>}>
      <label className="check"><input type="checkbox" checked={s.enabled} onChange={e => setS({ ...s, enabled: e.target.checked })} /><span><b>Autopilot on</b>: find matches and prepare submissions automatically</span></label>
      <div className="row3 mt">
        <Field label="Minimum match %" error={errors.minScore}><input className="inp" type="number" value={s.minScore} onChange={num('minScore')} /></Field>
        <Field label="New jobs / consultant / day" error={errors.maxNewPerConsultant}><input className="inp" type="number" value={s.maxNewPerConsultant} onChange={num('maxNewPerConsultant')} /></Field>
        <Field label="Run every (minutes)" error={errors.runEveryMinutes}><input className="inp" type="number" value={s.runEveryMinutes} onChange={num('runEveryMinutes')} /></Field>
      </div>
      <div className="row2">
        <Field label="Prepare per run (AI calls)" error={errors.maxPreparePerRun}><input className="inp" type="number" value={s.maxPreparePerRun} onChange={num('maxPreparePerRun')} /></Field>
        <Field label="Max auto follow-ups / thread" error={errors.maxAutoFollowUps}><input className="inp" type="number" value={s.maxAutoFollowUps} onChange={num('maxAutoFollowUps')} /></Field>
      </div>
      <label className="check"><input type="checkbox" checked={s.includeExternalJobs} onChange={e => setS({ ...s, includeExternalJobs: e.target.checked })} /><span>Include saved external jobs (LinkedIn, Indeed…)</span></label>
      <label className="check"><input type="checkbox" checked={s.autoFollowUp} onChange={e => setS({ ...s, autoFollowUp: e.target.checked })} /><span>Follow up with vendors automatically when there's no reply in 48 hours</span></label>
      <label className="check"><input type="checkbox" checked={s.classifyReplies} onChange={e => setS({ ...s, classifyReplies: e.target.checked })} /><span>Read vendor replies and suggest the next step</span></label>
      <label className="check"><input type="checkbox" checked={s.emailNotifications} onChange={e => setS({ ...s, emailNotifications: e.target.checked })} /><span>Email recruiters about Autopilot events (each person chooses which under Email alerts)</span></label>
      <p className="muted mt" style={{ fontSize: 12.5 }}>Nothing reaches a vendor until a recruiter approves it and the consultant e-signs the RTR. Follow-ups go from the recruiter's own Outlook.</p>
    </Modal>
  );
}

/** The signed-in user's Autopilot email alerts: one combined email per run, only for the kinds they pick. */
function AlertsDialog({ onClose }: { onClose: () => void }) {
  const onError = useErrorHandler();
  const notify = useToast();
  const [p, setP] = useState<AlertPrefs | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { api<AlertPrefs>('/autopilot/notifications').then(setP).catch(e => { onError(e); onClose(); }); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const save = async () => {
    if (!p) return;
    setBusy(true);
    try { setP(await api<AlertPrefs>('/autopilot/notifications', { method: 'PUT', body: p })); notify('success', 'Email alerts saved', p.enabled ? "You'll get one email when there's news, not one per event." : 'Autopilot emails are off for you.'); onClose(); }
    catch (e) { onError(e); }
    finally { setBusy(false); }
  };
  const opts: [keyof AlertPrefs, string, string][] = [
    ['approvals', 'Ready for my approval', 'New submissions the Autopilot prepared for my consultants'],
    ['vendorReplies', 'Vendor replies', 'Interview requests, rejections, closed positions, info and rate questions'],
    ['rtrUpdates', 'RTR and submissions', 'Submitted after the consultant signed, RTR declined or expired'],
    ['problems', 'Problems', "A consultant needs a resume, or a submission couldn't be sent"],
    ['followUps', 'Follow-ups sent for me', 'Each automatic follow-up the Autopilot sent from my Outlook'],
  ];
  return (
    <Modal title="Autopilot email alerts" onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save} disabled={busy || !p}>{busy ? <Spinner /> : 'Save'}</button></>}>
      {!p ? <Empty><Spinner dark /> Loading…</Empty> : <>
        {!p.canReceive && <Alert type="warning" title="Alerts can't reach you yet">{p.reason}</Alert>}
        <label className="check"><input type="checkbox" checked={p.enabled} onChange={e => setP({ ...p, enabled: e.target.checked })} /><span><b>Email me about the Autopilot</b></span></label>
        {opts.map(([k, label, hint]) => (
          <label key={k} className="check" style={{ opacity: p.enabled ? 1 : 0.5 }}>
            <input type="checkbox" disabled={!p.enabled} checked={Boolean(p[k])} onChange={e => setP({ ...p, [k]: e.target.checked })} />
            <span>{label}<small className="muted" style={{ display: 'block', fontSize: 12 }}>{hint}</small></span></label>))}
        <p className="muted mt" style={{ fontSize: 12.5 }}>
          Everything new is combined into one email per Autopilot run, so you're not flooded. {p.sendsFrom ? <>Alerts come from <b>{p.sendsFrom}</b>.</> : 'Alerts are sent from your own Outlook to you.'}
          {p.lastSentUtc ? ` Last alert sent ${ago(p.lastSentUtc)}.` : ''}
        </p>
      </>}
    </Modal>
  );
}
