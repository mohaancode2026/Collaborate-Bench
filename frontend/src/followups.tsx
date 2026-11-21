import { useCallback, useEffect, useState } from 'react';
import { api, Reminder, VendorEmail } from './api';
import { useAuth } from './auth';
import { Empty, Field, FieldErrors, Modal, Spinner, ago, useErrorHandler, useToast } from './ui';

/** Tell the sidebar badge (and anything else listening) that reminders changed. */
export const remindersChanged = () => window.dispatchEvent(new Event('cbs:reminders'));

/** Count of your follow-ups that are due now, refreshed every 2 minutes and after any change. */
export function useDueReminders() {
  const { can, user } = useAuth();
  const [due, setDue] = useState(0);
  useEffect(() => {
    if (!user || !can('mail.view')) { setDue(0); return; }
    let alive = true;
    const load = () => api<{ due: number }>('/mail/reminders/count').then(r => { if (alive) setDue(r.due); }).catch(() => {});
    load();
    const t = setInterval(load, 120_000);
    window.addEventListener('cbs:reminders', load);
    return () => { alive = false; clearInterval(t); window.removeEventListener('cbs:reminders', load); };
  }, [user, can]);
  return due;
}

const at9 = (d: Date) => { d.setHours(9, 0, 0, 0); return d; };
const addDays = (n: number) => { const d = new Date(); d.setDate(d.getDate() + n); return d; };
const nextMonday = () => { const d = new Date(); d.setDate(d.getDate() + ((8 - d.getDay()) % 7 || 7)); return at9(d); };
export const QUICK: [string, () => Date][] = [
  ['In 3 hours', () => new Date(Date.now() + 3 * 3600_000)],
  ['Tomorrow 9 AM', () => at9(addDays(1))],
  ['In 2 days', () => at9(addDays(2))],
  ['In 3 days', () => at9(addDays(3))],
  ['Next Monday 9 AM', nextMonday],
];
const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
const dueText = (r: Reminder) => {
  const ms = new Date(r.dueUtc).getTime() - Date.now();
  if (ms <= 0) return `Due ${ago(r.dueUtc)}`;
  const h = Math.round(ms / 3600_000);
  return h < 24 ? `Due in ${Math.max(1, h)}h` : `Due ${fmt(r.dueUtc)}`;
};

/** "Remind me to follow up" on one of your vendor emails. */
export function RemindDialog({ email, onClose, onSaved }: { email: VendorEmail; onClose: () => void; onSaved?: () => void }) {
  const notify = useToast();
  const onError = useErrorHandler();
  const [when, setWhen] = useState<Date>(QUICK[1][1]());
  const [custom, setCustom] = useState('');
  const [note, setNote] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const save = async () => {
    const due = custom ? new Date(custom) : when;
    if (isNaN(due.getTime())) { setErrors({ dueUtc: ['Pick a date and time'] }); return; }
    setBusy(true); setErrors({});
    try {
      await api('/mail/reminders', { method: 'POST', body: { emailId: email.id, dueUtc: due.toISOString(), note } });
      notify('success', 'Reminder set', `We'll remind you ${fmt(due.toISOString())}. It clears itself if ${email.vendor} replies first.`);
      remindersChanged(); onSaved?.(); onClose();
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };
  return (
    <Modal title="Remind me to follow up" onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save} disabled={busy}>{busy ? <Spinner /> : 'Set reminder'}</button></>}>
      <p className="muted" style={{ marginBottom: 12 }}><b style={{ color: 'var(--ink)' }}>{email.subject}</b> · {email.vendor}</p>
      <Field label="When" error={errors.dueUtc}>
        <div className="chips">{QUICK.map(([label, f]) => {
          const on = !custom && Math.abs(f().getTime() - when.getTime()) < 60_000;
          return <button key={label} type="button" className={`skchip${on ? ' on' : ''}`} onClick={() => { setCustom(''); setWhen(f()); }}>{label}</button>;
        })}</div>
        <input className="inp" type="datetime-local" style={{ marginTop: 8, width: 'auto' }} value={custom} onChange={e => setCustom(e.target.value)} aria-label="Custom date and time" />
      </Field>
      <Field label="Note (optional)" error={errors.note}><input className="inp" value={note} onChange={e => setNote(e.target.value)} placeholder="e.g. Ask about client interview feedback" maxLength={500} /></Field>
    </Modal>
  );
}

/** Follow-ups tab: what's due, what's coming, and what was recently closed. */
export function FollowUpsPanel({ onFollowUp }: { onFollowUp: (r: Reminder) => void }) {
  const { user } = useAuth();
  const notify = useToast();
  const onError = useErrorHandler();
  const [view, setView] = useState<'open' | 'closed'>('open');
  const [team, setTeam] = useState(false);
  const [list, setList] = useState<Reminder[] | null>(null);
  const lead = user?.dataScope !== 'OWN';

  const load = useCallback(() => {
    setList(null);
    api<Reminder[]>('/mail/reminders', { query: { status: view, scope: team ? 'team' : undefined } }).then(setList).catch(e => { setList([]); onError(e); });
  }, [view, team, onError]);
  useEffect(load, [load]);
  useEffect(() => { window.addEventListener('cbs:reminders-reload', load); return () => window.removeEventListener('cbs:reminders-reload', load); }, [load]);

  const update = async (r: Reminder, body: { dueUtc?: string; status?: string }, msg: string) => {
    try { await api(`/mail/reminders/${r.id}`, { method: 'PATCH', body }); notify('success', msg); remindersChanged(); load(); }
    catch (e) { onError(e); }
  };

  const overdue = (list ?? []).filter(r => r.overdue), upcoming = (list ?? []).filter(r => !r.overdue);
  const STATUS: Record<string, [string, string]> = { Done: ['g', 'You followed up'], Replied: ['g', 'Vendor replied'], Dismissed: ['s', 'Dismissed'], Open: ['y', 'Open'] };

  const row = (r: Reminder) => (
    <div key={r.id} className="match" style={{ alignItems: 'flex-start', borderColor: r.overdue ? '#fcd34d' : undefined }}>
      <div className="mi" style={{ minWidth: 0 }}>
        <div className="flex" style={{ gap: 6 }}>
          <b>{r.subject}</b>
          {r.status === 'Open' ? <span className={`tag ${r.overdue ? 'y' : 's'}`}>{dueText(r)}</span> : <span className={`tag ${STATUS[r.status][0]}`}>{STATUS[r.status][1]}</span>}
          {r.auto && <span className="tag s" title="Created because the vendor hasn't replied">Auto</span>}
        </div>
        <small>{r.vendor} · {r.toAddresses.split(',')[0]} · sent {r.daysWaiting === 0 ? 'today' : `${r.daysWaiting} day${r.daysWaiting === 1 ? '' : 's'} ago`}
          {r.consultantName ? ` · ${r.consultantName}` : ''}{!r.mine ? ` · ${r.userName}` : ''}</small>
        {r.note && <div className="why">📝 {r.note}</div>}
        {!r.note && r.preview && <div className="why" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.preview}</div>}
      </div>
      {r.mine && <div className="flex" style={{ flexWrap: 'nowrap' }}>
        {r.status === 'Open' ? <>
          <button className="btn btn-sm btn-primary" onClick={() => onFollowUp(r)}>Follow up</button>
          <select className="inp" style={{ width: 'auto', padding: '5px 8px', fontSize: 13 }} value="" aria-label="Snooze"
            onChange={e => { const f = QUICK.find(q => q[0] === e.target.value)?.[1]; if (f) update(r, { dueUtc: f().toISOString() }, 'Reminder snoozed'); }}>
            <option value="">Snooze…</option>{QUICK.map(([l]) => <option key={l}>{l}</option>)}</select>
          <button className="btn btn-sm btn-outline" title="I followed up another way (phone, chat…)" onClick={() => update(r, { status: 'Done' }, 'Marked as done')}>Done</button>
          <button className="btn btn-sm" style={{ color: 'var(--muted)' }} onClick={() => update(r, { status: 'Dismissed' }, 'Reminder dismissed')}>Dismiss</button>
        </> : <button className="btn btn-sm btn-outline" onClick={() => update(r, { status: 'Open' }, 'Reminder reopened')}>Reopen</button>}
      </div>}
    </div>);

  return (
    <div className="card-b">
      <div className="flex between" style={{ marginBottom: 12 }}>
        <div className="tabs"><button className={view === 'open' ? 'on' : ''} onClick={() => setView('open')}>Open</button><button className={view === 'closed' ? 'on' : ''} onClick={() => setView('closed')}>Closed (30 days)</button></div>
        {lead && <label className="flex" style={{ fontSize: 13 }}><input type="checkbox" checked={team} onChange={e => setTeam(e.target.checked)} />Show my team's follow-ups</label>}
      </div>
      <p className="muted" style={{ fontSize: 12.5, marginBottom: 12 }}>Emails you send that get no vendor reply in 48 hours get a reminder automatically. Reminders close by themselves when the vendor replies or you follow up in the same thread.</p>
      {list === null ? <Empty><Spinner dark /> Loading follow-ups…</Empty> : list.length === 0
        ? <Empty>{view === 'open' ? 'No follow-ups pending. Nice work.' : 'Nothing closed in the last 30 days.'}</Empty>
        : view === 'closed' ? list.map(row) : <>
          {overdue.length > 0 && <><h4 className="fu-h">Due now ({overdue.length})</h4>{overdue.map(row)}</>}
          {upcoming.length > 0 && <><h4 className="fu-h">Coming up ({upcoming.length})</h4>{upcoming.map(row)}</>}
        </>}
    </div>
  );
}
