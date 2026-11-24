import { useCallback, useEffect, useState } from 'react';
import { api, MailConnection, MailSummary, Reminder, VendorEmail, VendorInfo } from '../api';
import { useAuth } from '../auth';
import { ComposeDialog } from '../compose';
import { TemplatesPanel } from '../templates';
import { FollowUpsPanel, RemindDialog, remindersChanged, useDueReminders } from '../followups';
import { Alert, Empty, Field, FieldErrors, Modal, Spinner, ago, useConfirm, useErrorHandler, useToast } from '../ui';

type Tab = 'activity' | 'followups' | 'vendors' | 'recruiters' | 'directory' | 'templates';
const when = (iso: string) => new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

export default function VendorMail() {
  const { can, scope, user } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [conn, setConn] = useState<MailConnection | null>(null);
  const [days, setDays] = useState(30);
  const [summary, setSummary] = useState<MailSummary | null>(null);
  const [tab, setTab] = useState<Tab>(() => (new URLSearchParams(window.location.search).get('tab') === 'followups' ? 'followups' : 'activity'));
  const due = useDueReminders();
  const [remind, setRemind] = useState<VendorEmail | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [f, setF] = useState({ q: '', vendorId: '', userId: '', direction: '' });
  const [emails, setEmails] = useState<VendorEmail[] | null>(null);
  const [edit, setEdit] = useState<VendorInfo | 'new' | null>(null);
  const [newName, setNewName] = useState('');
  const [compose, setCompose] = useState<{ replyTo?: VendorEmail; to?: string; followUp?: boolean; consultantId?: number | null } | null>(null);
  const [discover, setDiscover] = useState(false);
  const team = user?.dataScope !== 'OWN' && scope !== 'mine';

  const loadConn = useCallback(() => { api<MailConnection>('/mail/connection').then(setConn).catch(onError); }, [onError]);
  const loadSummary = useCallback(() => { api<MailSummary>('/mail/summary', { query: { scope, days } }).then(setSummary).catch(onError); }, [scope, days, onError]);
  useEffect(loadConn, [loadConn]);
  useEffect(loadSummary, [loadSummary]);

  const loadEmails = useCallback(() => {
    if (tab !== 'activity') return;
    setEmails(null);
    api<VendorEmail[]>('/mail/emails', { query: { scope, days, q: f.q, vendorId: f.vendorId, userId: f.userId, direction: f.direction, } })
      .then(setEmails).catch(e => { setEmails([]); onError(e); });
  }, [tab, scope, days, f, onError]);
  useEffect(loadEmails, [loadEmails]);

  // Back from connecting Outlook.
  useEffect(() => {
    const q = new URLSearchParams(window.location.search);
    if (q.get('connected')) notify('success', 'Outlook connected', 'Reading the last 30 days of vendor emails. This can take a minute.');
    else if (q.get('msError')) notify(q.get('msError') === 'cancelled' ? 'info' : 'error', q.get('msError') === 'cancelled' ? 'Outlook connection cancelled' : "Couldn't connect Outlook",
      q.get('msError') === 'cancelled' ? 'Nothing was changed.' : 'Microsoft sign-in failed. Try again, or ask your admin to check the app registration.');
    if (q.get('connected') || q.get('msError')) window.history.replaceState(null, '', '/vendor-mail');
  }, [notify]);

  const connect = async () => {
    try { window.location.assign((await api<{ url: string }>('/auth/microsoft/start', { method: 'POST', body: { mode: 'connect' } })).url); }
    catch (e) { onError(e); }
  };
  const syncNow = async () => {
    setSyncing(true);
    try {
      const r = await api<{ read: number; tracked: number; status: string; error?: string }>('/mail/sync', { method: 'POST' });
      if (r.error) notify('warning', r.status === 'ReconnectNeeded' ? 'Reconnect Outlook' : 'Sync problem', r.error);
      else notify('success', 'Outlook synced', r.tracked ? `${r.tracked} new vendor email${r.tracked === 1 ? '' : 's'} tracked.` : `Checked ${r.read} email${r.read === 1 ? '' : 's'}. Nothing new from vendors.`);
      loadConn(); loadSummary(); loadEmails();
    } catch (e) { onError(e); }
    finally { setSyncing(false); }
  };
  const disconnect = async () => {
    if (!await confirm({ title: 'Stop tracking Outlook?', message: 'New vendor emails will no longer be tracked. Emails already tracked stay in the history.', okLabel: 'Disconnect', danger: true })) return;
    try { await api('/mail/connection', { method: 'DELETE' }); notify('success', 'Outlook disconnected'); loadConn(); } catch (e) { onError(e); }
  };
  const showVendor = (id: number) => { setF({ ...f, vendorId: String(id) }); setTab('activity'); };
  const showRecruiter = (id: number) => { setF({ ...f, userId: String(id) }); setTab('activity'); };

  const tabs: [Tab, string][] = [['activity', 'Activity'], ['followups', `Follow-ups${due ? ` (${due} due)` : ''}`], ['vendors', 'By vendor'],
    ...(team ? [['recruiters', 'By recruiter'] as [Tab, string]] : []), ['directory', 'Vendor list'], ['templates', 'Templates']];

  return (
    <>
      <div className="ph"><div><h1>Vendor Emails</h1><p>Emails between your team and vendors, tracked from Outlook. Only a summary is kept: sender, recipients, subject, first lines and attachment names.</p></div>
        <div className="flex">
          {conn?.configured && <button className="btn btn-dark" onClick={() => setCompose({})}>✉ New email</button>}
          <select className="inp" style={{ width: 'auto' }} value={days} onChange={e => setDays(Number(e.target.value))} aria-label="Period">
            {[7, 30, 90].map(d => <option key={d} value={d}>Last {d} days</option>)}</select></div></div>

      {conn && <ConnectionCard conn={conn} syncing={syncing} onConnect={connect} onSync={syncNow} onDisconnect={disconnect} />}
      {conn?.configured && conn.vendors === 0 && (
        <Alert type="warning" title="No vendors set up yet">
          Emails are only tracked for vendors in the vendor list. {can('vendors.manage') ? <>Let the app find them in your Outlook, or add them under "Vendor list".
            <button className="btn btn-sm btn-primary" style={{ marginLeft: 8 }} onClick={() => setDiscover(true)}>✦ Find vendors in Outlook</button></>
            : <>Ask your Team Lead to add vendors and their email domains.{conn.connected && <> You can see which vendors you work with first.
              <button className="btn btn-sm btn-primary" style={{ marginLeft: 8 }} onClick={() => setDiscover(true)}>✦ Vendors in my Outlook</button></>}</>}
        </Alert>)}

      <div className="grid g4" style={{ margin: '16px 0' }}>
        <div className="card kpi dark"><small>Sent to vendors</small><b>{summary?.sent ?? '…'}</b><span className="trend">last {days} days</span></div>
        <div className="card kpi"><small>Received from vendors</small><b>{summary?.received ?? '…'}</b><span className="trend">last {days} days</span></div>
        <div className="card kpi"><small>Vendors contacted</small><b>{summary?.vendorsContacted ?? '…'}</b><span className="trend">of {summary?.byVendor.filter(v => v.active).length ?? 0} in the list</span></div>
        <div className="card kpi"><small>Awaiting vendor reply</small><b>{summary?.awaitingReply ?? '…'}</b><span className="trend" style={{ color: summary?.awaitingReply ? 'var(--warn, #b45309)' : undefined }}>no reply in 48 hours</span></div>
      </div>

      <div className="card">
        <div className="card-h">
          <div className="tabs">{tabs.map(([k, t]) => <button key={k} className={tab === k ? 'on' : ''} onClick={() => setTab(k)}>{t}</button>)}</div>
          {tab === 'activity' && (
            <div className="flex">
              <input className="inp" style={{ width: 200, padding: '7px 10px' }} placeholder="Search subject, email, consultant" value={f.q} onChange={e => setF({ ...f, q: e.target.value })} />
              <select className="inp" style={{ width: 'auto', padding: '7px 10px' }} value={f.vendorId} onChange={e => setF({ ...f, vendorId: e.target.value })} aria-label="Vendor">
                <option value="">All vendors</option>{summary?.byVendor.map(v => <option key={v.id} value={v.id}>{v.name}</option>)}</select>
              {team && <select className="inp" style={{ width: 'auto', padding: '7px 10px' }} value={f.userId} onChange={e => setF({ ...f, userId: e.target.value })} aria-label="Recruiter">
                <option value="">Everyone</option>{summary?.byRecruiter.map(r => <option key={r.userId} value={r.userId}>{r.name}</option>)}</select>}
              {tab === 'activity' && <select className="inp" style={{ width: 'auto', padding: '7px 10px' }} value={f.direction} onChange={e => setF({ ...f, direction: e.target.value })} aria-label="Direction">
                <option value="">Sent and received</option><option value="Out">Sent to vendor</option><option value="In">From vendor</option></select>}
            </div>)}
          {tab === 'directory' && (can('vendors.manage') || conn?.connected) && <div className="flex">
            {conn?.configured && (can('vendors.manage') || conn.connected) &&
              <button className="btn btn-sm btn-outline" onClick={() => setDiscover(true)}>{can('vendors.manage') ? '✦ Find vendors in Outlook' : '✦ Vendors in my Outlook'}</button>}
            {can('vendors.manage') && <button className="btn btn-sm btn-primary" onClick={() => { setNewName(''); setEdit('new'); }}>+ Add vendor</button>}</div>}
        </div>

        {tab === 'activity' && <EmailTable emails={emails} team={team} awaiting={false} meId={user?.id}
          onReply={conn?.configured ? e => setCompose({ replyTo: e, followUp: e.awaitingReply, consultantId: e.consultantId }) : undefined} onRemind={setRemind} />}
        {tab === 'followups' && <FollowUpsPanel onFollowUp={(r: Reminder) => setCompose({ followUp: true, consultantId: r.consultantId,
          replyTo: { id: r.emailId, direction: 'Out', subject: r.subject, toAddresses: r.toAddresses, fromAddress: '' } as VendorEmail })} />}

        {tab === 'vendors' && <div className="tw"><table><thead><tr><th>Vendor</th><th>Sent</th><th>Received</th><th>Awaiting reply</th><th>Last contact</th></tr></thead><tbody>
          {summary?.byVendor.filter(v => v.active || v.sent + v.received > 0).map(v => (
            <tr key={v.id} style={{ cursor: 'pointer' }} onClick={() => showVendor(v.id)}>
              <td><b>{v.name}</b><div className="muted" style={{ fontSize: 12 }}>{v.domains.join(', ')}</div></td>
              <td>{v.sent}</td><td>{v.received}</td><td>{v.awaitingReply ? <span className="tag y">{v.awaitingReply}</span> : '—'}</td>
              <td>{v.lastContactUtc ? ago(v.lastContactUtc) : <span className="muted">No contact in {days} days</span>}</td></tr>))}
          {summary?.byVendor.length === 0 && <tr><td colSpan={5}><Empty>No vendors in the list yet.</Empty></td></tr>}
        </tbody></table></div>}

        {tab === 'recruiters' && <div className="tw"><table><thead><tr><th>Recruiter</th><th>Outlook</th><th>Sent</th><th>Received</th><th>Vendors</th><th>Awaiting reply</th><th>Last contact</th></tr></thead><tbody>
          {summary?.byRecruiter.map(r => (
            <tr key={r.userId} style={{ cursor: 'pointer' }} onClick={() => showRecruiter(r.userId)}>
              <td><b>{r.name}</b></td>
              <td>{!r.mailbox ? <span className="tag s">Not connected</span> : r.mailStatus === 'Active' ? <span className="tag g" title={r.mailbox}>Connected</span> : <span className="tag r" title={r.mailbox}>Reconnect needed</span>}</td>
              <td>{r.sent}</td><td>{r.received}</td><td>{r.vendors}</td><td>{r.awaitingReply ? <span className="tag y">{r.awaitingReply}</span> : '—'}</td>
              <td>{r.lastContactUtc ? ago(r.lastContactUtc) : '—'}</td></tr>))}
        </tbody></table></div>}

        {tab === 'templates' && <TemplatesPanel />}

        {tab === 'directory' && <div className="card-b">
          {can('vendors.manage') && <Suggestions onPick={n => { setNewName(n); setEdit('new'); }} refreshKey={summary} />}
          <div className="tw"><table><thead><tr><th>Vendor</th><th>Email domains and addresses</th><th>Status</th>{can('vendors.manage') && <th />}</tr></thead><tbody>
            {summary?.byVendor.map(v => (
              <tr key={v.id}><td><b>{v.name}</b></td><td>{v.domains.map(d => <span key={d} className="tag s mono">{d}</span>)}</td>
                <td>{v.active ? <span className="tag g">Tracked</span> : <span className="tag s">Inactive</span>}</td>
                {can('vendors.manage') && <td><button className="btn btn-sm btn-outline" onClick={() => setEdit(v)}>Edit</button></td>}</tr>))}
            {summary?.byVendor.length === 0 && <tr><td colSpan={4}><Empty>No vendors yet.{can('vendors.manage') ? ' Add one, or pick a suggestion above.' : ''}</Empty></td></tr>}
          </tbody></table></div>
        </div>}
      </div>

      {compose && <ComposeDialog replyTo={compose.replyTo} initial={compose.to ? { to: compose.to } : undefined} onClose={() => setCompose(null)}
        templateCategory={compose.followUp ? 'Follow-up' : undefined} consultantId={compose.consultantId}
        onSent={() => { setTimeout(() => { loadSummary(); loadEmails(); loadConn(); remindersChanged(); window.dispatchEvent(new Event('cbs:reminders-reload')); }, 5000); }} />}
      {remind && <RemindDialog email={remind} onClose={() => setRemind(null)} />}
      {edit && <VendorDialog vendor={edit === 'new' ? null : edit} initialName={newName} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); loadSummary(); loadConn(); }} />}
      {discover && <DiscoverDialog canAdd={can('vendors.manage')} ownOnly={user?.dataScope === 'OWN'} onClose={() => setDiscover(false)} onAdded={() => {
        setDiscover(false); setTab('directory'); loadSummary(); loadConn();
        // The re-check of the mailboxes runs in the background; refresh the numbers once it has had time to finish.
        setTimeout(() => { loadSummary(); loadEmails(); }, 20_000);
      }} />}
    </>
  );
}

function ConnectionCard({ conn, syncing, onConnect, onSync, onDisconnect }: { conn: MailConnection; syncing: boolean; onConnect: () => void; onSync: () => void; onDisconnect: () => void }) {
  if (!conn.configured) return (
    <Alert type="info" title="Outlook tracking isn't set up yet">
      Your admin needs to register the app in Microsoft Entra ID and add Microsoft:TenantId, ClientId and ClientSecret to the API settings. Vendor emails already tracked are shown below.
    </Alert>);
  if (!conn.connected) return (
    <div className="card"><div className="card-b flex between">
      <div><b>Track your vendor emails</b><div className="muted" style={{ fontSize: 13 }}>Connect your company Outlook mailbox. Only emails with vendors in the vendor list are tracked.</div></div>
      <button className="btn btn-primary" onClick={onConnect}>Connect Outlook</button>
    </div></div>);
  const bad = conn.status !== 'Active';
  return (
    <div className="card" style={bad ? { borderColor: '#fca5a5' } : undefined}><div className="card-b flex between">
      <div>
        <b>{bad ? 'Outlook needs to be reconnected' : 'Outlook connected'}</b> <span className="muted mono" style={{ fontSize: 12.5 }}>{conn.mailbox}</span>
        <div className="muted" style={{ fontSize: 13 }}>{bad ? conn.lastError : conn.lastSyncUtc ? `Last checked ${ago(conn.lastSyncUtc)}. New mail is checked every few minutes.` : 'First sync in progress…'}</div>
      </div>
      <div className="flex">
        {bad ? <button className="btn btn-primary" onClick={onConnect}>Reconnect Outlook</button>
          : <button className="btn btn-outline" onClick={onSync} disabled={syncing}>{syncing ? <Spinner dark /> : 'Sync now'}</button>}
        <button className="btn btn-sm" style={{ color: 'var(--muted)' }} onClick={onDisconnect}>Disconnect</button>
      </div>
    </div></div>);
}

function EmailTable({ emails, team, awaiting, meId, onReply, onRemind }: { emails: VendorEmail[] | null; team: boolean; awaiting: boolean; meId?: number; onReply?: (e: VendorEmail) => void; onRemind?: (e: VendorEmail) => void }) {
  if (emails === null) return <Empty><Spinner dark /> Loading emails…</Empty>;
  if (emails.length === 0) return <Empty>{awaiting ? 'Nothing waiting. Every email sent to a vendor got a reply within 48 hours.' : 'No vendor emails in this period.'}</Empty>;
  return (
    <div className="tw"><table><thead><tr><th>When</th><th /><th>Vendor</th><th>Email</th><th>Consultant</th>{team && <th>Recruiter</th>}<th /></tr></thead><tbody>
      {emails.map(e => (
        <tr key={e.id} className="mailrow">
          <td style={{ whiteSpace: 'nowrap' }}>{when(e.sentUtc)}</td>
          <td><span className={`dir ${e.direction === 'Out' ? 'out' : 'in'}`} title={e.direction === 'Out' ? 'Sent to vendor' : 'Received from vendor'}>{e.direction === 'Out' ? '↗' : '↙'}</span></td>
          <td><b>{e.vendor}</b><div className="muted" style={{ fontSize: 12 }}>{e.direction === 'In' ? (e.fromName || e.fromAddress) : e.toAddresses.split(',')[0]}</div></td>
          <td>
            <div className="subj">{e.subject} {e.awaitingReply && <span className="tag y">Awaiting reply</span>}</div>
            <div className="prev" title={e.preview}>{e.preview}</div>
            {e.attachments.length > 0 && <div style={{ marginTop: 2 }}>{e.attachments.slice(0, 3).map(a => <span key={a} className="tag s">📎 {a}</span>)}</div>}
          </td>
          <td>{e.consultantName ?? <span className="muted">—</span>}</td>
          {team && <td>{e.userName}</td>}
          <td style={{ whiteSpace: 'nowrap' }}>{e.userId === meId && <>
            {onReply && <button className="btn btn-sm btn-outline" onClick={() => onReply(e)}>{e.awaitingReply ? 'Follow up' : 'Reply'}</button>}
            {onRemind && <button className="btn btn-sm" title="Remind me to follow up" aria-label="Remind me to follow up" onClick={() => onRemind(e)}>⏰</button>}
          </>}</td>
        </tr>))}
    </tbody></table></div>);
}

function Suggestions({ onPick, refreshKey }: { onPick: (name: string) => void; refreshKey: unknown }) {
  const [names, setNames] = useState<string[]>([]);
  useEffect(() => { api<string[]>('/vendors/suggestions').then(setNames).catch(() => setNames([])); }, [refreshKey]);
  if (names.length === 0) return null;
  return (
    <div className="flex" style={{ marginBottom: 14 }}>
      <span className="muted" style={{ fontSize: 13 }}>Vendors from your requirements, not in the list yet:</span>
      {names.slice(0, 12).map(n => <button key={n} className="skchip" onClick={() => onPick(n)}>+ {n}</button>)}
    </div>);
}

interface Discovered {
  key: string; isAddress: boolean; likelyConsultant: boolean; suggestedName: string; score: number; received: number; sent: number; mailboxes: number;
  lastSeenUtc: string; contacts: { address: string; name?: string | null; emails: number }[]; signals: string[]; sampleSubjects: string[];
}
interface DiscoveryResult { mailboxes: number; messagesRead: number; days: number; vendors: Discovered[]; problems: string[] }
interface BatchResult { created: { id: number; name: string }[]; failed: { name: string; domains: string; error: string }[] }

/** Strong matches are ticked to start with; the rest are shown so nothing is missed. */
const PRESELECT = 50;

/**
 * @param canAdd  Team Leads and above pick vendors and add them; Recruiters get a read-only view.
 * @param ownOnly The caller can only scan their own mailbox (Recruiters), so the "Only my mailbox" switch is hidden.
 */
function DiscoverDialog({ canAdd, ownOnly, onClose, onAdded }: { canAdd: boolean; ownOnly: boolean; onClose: () => void; onAdded: () => void }) {
  const notify = useToast();
  const onError = useErrorHandler();
  const [days, setDays] = useState(30);
  const [mine, setMine] = useState(ownOnly);
  const [res, setRes] = useState<DiscoveryResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const [names, setNames] = useState<Record<string, string>>({});
  const [q, setQ] = useState('');
  const [kind, setKind] = useState<'company' | 'personal'>('company');
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<BatchResult['failed']>([]);

  useEffect(() => {
    let live = true;
    setLoading(true); setFailed([]);
    api<DiscoveryResult>('/vendors/discover', { query: { days, mine: mine ? 'true' : undefined } })
      .then(r => {
        if (!live) return;
        setRes(r);
        setPicked(Object.fromEntries(r.vendors.map(v => [v.key, v.score >= PRESELECT && !v.isAddress])));
        setNames(Object.fromEntries(r.vendors.map(v => [v.key, v.suggestedName])));
      })
      .catch(e => { if (live) { setRes(null); onError(e); } })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [days, mine, onError]);

  const needle = q.trim().toLowerCase();
  const companies = (res?.vendors ?? []).filter(v => !v.isAddress).length;
  const personal = (res?.vendors ?? []).length - companies;
  const rows = (res?.vendors ?? []).filter(v => v.isAddress === (kind === 'personal')).filter(v => !needle || v.key.includes(needle) || (names[v.key] ?? '').toLowerCase().includes(needle)
    || v.contacts.some(c => (c.name ?? '').toLowerCase().includes(needle)));
  const chosen = (res?.vendors ?? []).filter(v => picked[v.key]);
  const allShownPicked = rows.length > 0 && rows.every(v => picked[v.key]);

  const add = async () => {
    setBusy(true); setFailed([]);
    try {
      const r = await api<BatchResult>('/vendors/batch', { method: 'POST', body: { vendors: chosen.map(v => ({ name: (names[v.key] ?? v.suggestedName).trim(), domains: [v.key] })) } });
      if (r.created.length) notify('success', `${r.created.length} vendor${r.created.length === 1 ? '' : 's'} added`,
        `Connected mailboxes are being re-checked for the last 30 days of emails with ${r.created.length === 1 ? 'it' : 'them'}.`);
      if (r.failed.length) { setFailed(r.failed); setPicked(p => Object.fromEntries(Object.entries(p).filter(([k]) => r.failed.some(f => f.domains === k)))); }
      else onAdded();
    } catch (e) { onError(e); }
    finally { setBusy(false); }
  };

  return (
    <Modal title={canAdd ? 'Find vendors in Outlook' : 'Vendors in my Outlook'} wide onClose={onClose}
      footer={canAdd ? <>
        <span className="muted" style={{ marginRight: 'auto', fontSize: 13 }}>{chosen.length} selected</span>
        <button className="btn btn-outline" onClick={onClose}>{failed.length ? 'Done' : 'Cancel'}</button>
        <button className="btn btn-primary" onClick={add} disabled={busy || loading || chosen.length === 0}>{busy ? <Spinner /> : `Add ${chosen.length || ''} vendor${chosen.length === 1 ? '' : 's'}`}</button></>
        : <button className="btn btn-primary" onClick={onClose}>Close</button>}>
      <p className="muted" style={{ fontSize: 13, marginTop: 0 }}>
        {canAdd
          ? `Outside companies ${mine ? 'you email' : 'your team emails'} about requirements, rates, C2C, RTR and submissions. Only senders, recipients, subjects and first lines are read, and nothing is saved until you add vendors.`
          : 'Vendors you email about requirements, rates, C2C, RTR and submissions, found in your own mailbox. Only senders, recipients, subjects and first lines are read, and nothing is saved.'}
      </p>
      {!canAdd && res && res.vendors.length > 0 && <Alert type="info" title="These aren't tracked yet">
        Only vendors in the vendor list are tracked. Ask your Team Lead to add the ones you work with, using “Find vendors in Outlook”.</Alert>}
      <div className="flex" style={{ margin: '10px 0' }}>
        <div className="tabs" aria-label="Kind">
          <button className={kind === 'company' ? 'on' : ''} onClick={() => setKind('company')}>Companies{res ? ` (${companies})` : ''}</button>
          <button className={kind === 'personal' ? 'on' : ''} onClick={() => setKind('personal')} title="Recruiters who use Gmail, Yahoo, etc. Only that one address is tracked.">Personal emails{res ? ` (${personal})` : ''}</button>
        </div>
        <select className="inp" style={{ width: 'auto', padding: '7px 10px' }} value={days} onChange={e => setDays(Number(e.target.value))} aria-label="Period" disabled={loading}>
          {[30, 60, 90].map(d => <option key={d} value={d}>Last {d} days</option>)}</select>
        <input className="inp" style={{ width: 220, padding: '7px 10px' }} placeholder="Filter by name or domain" value={q} onChange={e => setQ(e.target.value)} />
        {!ownOnly && <label className="check" style={{ margin: 0 }}><input type="checkbox" checked={mine} disabled={loading} onChange={e => setMine(e.target.checked)} /><span>Only my mailbox</span></label>}
        {res && !loading && <span className="muted" style={{ fontSize: 12.5 }}>Read {res.messagesRead.toLocaleString()} emails from {res.mailboxes} mailbox{res.mailboxes === 1 ? '' : 'es'}.</span>}
      </div>
      {res?.problems.length ? <Alert type="warning" title="Some mailboxes couldn't be read">{res.problems.join(' · ')}</Alert> : null}
      {failed.length > 0 && <Alert type="error" title={`${failed.length} couldn't be added`}>{failed.map(f => `${f.name} (${f.domains}): ${f.error}`).join(' · ')}</Alert>}

      {loading ? <Empty><Spinner dark /> Reading mailboxes… this can take a minute or two.</Empty>
        : !res || res.vendors.length === 0 ? <Empty>No new vendors found in the last {days} days{canAdd ? '. Try a longer period, or add vendors by hand.' : '. Vendors already in the vendor list aren\'t shown here.'}</Empty>
        : <div className="tw" style={{ maxHeight: '55vh', overflowY: 'auto' }}><table className="disc-grid"><thead><tr>
            {canAdd && <th style={{ width: 32 }}><input type="checkbox" aria-label="Select all shown" checked={allShownPicked}
              onChange={e => setPicked(p => ({ ...p, ...Object.fromEntries(rows.map(v => [v.key, e.target.checked])) }))} /></th>}
            <th>Vendor name</th><th>Domain / address</th><th>Emails</th><th>Why it looks like a vendor</th><th>Match</th></tr></thead><tbody>
          {rows.map(v => (
            <tr key={v.key} className={canAdd && picked[v.key] ? 'on' : undefined}>
              {canAdd && <td><input type="checkbox" aria-label={`Add ${names[v.key]}`} checked={!!picked[v.key]} onChange={e => setPicked(p => ({ ...p, [v.key]: e.target.checked }))} /></td>}
              <td style={{ minWidth: 170 }}>{canAdd
                ? <input className="inp" style={{ padding: '6px 9px' }} aria-label={`Name for ${v.key}`} value={names[v.key] ?? ''} onChange={e => setNames(n => ({ ...n, [v.key]: e.target.value }))} />
                : <b>{v.suggestedName}</b>}
                <small className="muted">{v.contacts.slice(0, 2).map(c => c.name || c.address).join(', ')}{v.contacts.length > 2 ? ` +${v.contacts.length - 2}` : ''}</small></td>
              <td><span className="mono" style={{ fontSize: 12.5 }}>{v.key}</span>
                {v.likelyConsultant && <div><span className="tag y" title="You mostly write to this address and rarely hear back, which is how consultants look. Add it only if it's a vendor recruiter.">Maybe a consultant</span></div>}</td>
              <td style={{ whiteSpace: 'nowrap' }}><span title="Received from them">↙ {v.received}</span> · <span title="Sent to them">↗ {v.sent}</span>
                <small className="muted">{ago(v.lastSeenUtc)}{v.mailboxes > 1 ? ` · ${v.mailboxes} recruiters` : ''}</small></td>
              <td style={{ minWidth: 220 }}>{v.signals.map(s => <span key={s} className="tag s">{s}</span>)}
                {v.sampleSubjects[0] && <small className="muted disc-subj" title={v.sampleSubjects.join('\n')}>“{v.sampleSubjects[0]}”</small>}</td>
              <td><span className={`tag ${v.score >= 70 ? 'g' : v.score >= PRESELECT ? 'c' : 's'}`}>{v.score}</span></td>
            </tr>))}
          {rows.length === 0 && <tr><td colSpan={canAdd ? 6 : 5}><Empty>{needle ? `Nothing matches “${q}”.` : kind === 'personal' ? 'No personal-email recruiters found.' : 'No vendor companies found.'}</Empty></td></tr>}
        </tbody></table></div>}
    </Modal>);
}

function VendorDialog({ vendor, initialName, onClose, onSaved }: { vendor: VendorInfo | null; initialName: string; onClose: () => void; onSaved: () => void }) {
  const notify = useToast();
  const onError = useErrorHandler();
  const [name, setName] = useState(vendor?.name ?? initialName);
  const [domains, setDomains] = useState(vendor?.domains.join(', ') ?? '');
  const [active, setActive] = useState(vendor?.active ?? true);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setErrors({});
    const body = { name, active, domains: domains.split(/[\s,;]+/).map(d => d.trim()).filter(Boolean) };
    try {
      if (vendor) await api(`/vendors/${vendor.id}`, { method: 'PUT', body });
      else await api('/vendors', { method: 'POST', body });
      notify('success', vendor ? 'Vendor updated' : 'Vendor added', 'Connected mailboxes are re-checked for the last 30 days of emails with this vendor.');
      onSaved();
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };
  return (
    <Modal title={vendor ? `Edit ${vendor.name}` : 'Add vendor'} onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save} disabled={busy}>{busy ? <Spinner /> : 'Save'}</button></>}>
      <Field label="Vendor name *" error={errors.name}><input className="inp" value={name} onChange={e => setName(e.target.value)} placeholder="e.g. TEKsystems" autoFocus /></Field>
      <Field label="Email domains or addresses *" error={errors.domains}>
        <textarea className="inp" style={{ minHeight: 70 }} value={domains} onChange={e => setDomains(e.target.value)} placeholder="teksystems.com, allegisgroup.com, raj.recruiter@gmail.com" /></Field>
      <p className="muted" style={{ fontSize: 12.5, marginTop: -6 }}>Use the company domain to track everyone at the vendor. For recruiters who use Gmail or Yahoo, add their full address, because a whole free-mail domain would track personal mail.</p>
      {vendor && <label className="check"><input type="checkbox" checked={active} onChange={e => setActive(e.target.checked)} /><span>Track emails with this vendor</span></label>}
    </Modal>);
}
