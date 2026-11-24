import { useState } from 'react';
import { api, InboxDraft, InboxTask } from './api';
import { ComposeDialog, ComposeInitial } from './compose';
import { Spinner, useErrorHandler, useToast } from './ui';

/** The button text for what the reminder's draft does. */
export function draftLabel(t: InboxTask) {
  const a = t.action ?? '';
  if (a === 'Nudge') return 'Open follow-up draft';
  if (a === 'Feedback') return 'Ask for feedback';
  if (a === 'AskConsultant') return 'Ask the consultant';
  if (a === 'Slots') return 'Send slots to consultant';
  if (a === 'Mock') return 'Plan mock session';
  if (a.startsWith('Forward:')) return `Forward to ${a.slice(8)}`;
  return t.inboxItemId ? 'Open reply draft' : null;
}

/**
 * "Open draft" on a reminder or task: opens the ready draft in the email window. Nothing goes out until the executive sends it
 * from there (or copies it into Outlook). Sending it marks the task done.
 */
export function TaskDraftButton({ t, onDone, primary }: { t: InboxTask; onDone?: () => void; primary?: boolean }) {
  const onError = useErrorHandler();
  const notify = useToast();
  const [busy, setBusy] = useState(false);
  const [compose, setCompose] = useState<{ initial: ComposeInitial; replyId?: number } | null>(null);
  const label = draftLabel(t);
  if (!label || t.status !== 'Open') return null;
  const open = async () => {
    setBusy(true);
    try {
      const r = await api<InboxDraft>(`/inbox/tasks/${t.id}/draft`, { method: 'POST' });
      if (r.notice) notify('info', 'Built-in engine wrote the draft', r.notice);
      setCompose({ initial: { to: r.to, subject: r.subject, body: r.body }, replyId: r.replyToInboxItemId ?? undefined });
    } catch (e) { onError(e); } finally { setBusy(false); }
  };
  const sent = async () => {
    setCompose(null);
    try { await api(`/inbox/tasks/${t.id}`, { method: 'PUT', body: { status: 'Done' } }); } catch { /* the email went; the task can be ticked by hand */ }
    notify('success', 'Sent', 'The task is marked done.');
    window.dispatchEvent(new Event('cbs:inbox'));
    onDone?.();
  };
  return (
    <>
      <button className={`btn btn-sm ${primary ? 'btn-primary' : 'btn-outline'}`} onClick={open} disabled={busy}>{busy ? <Spinner dark={!primary} /> : `✉ ${label}`}</button>
      {compose && <ComposeDialog title={label} initial={compose.initial} replyToInboxItemId={compose.replyId} onClose={() => setCompose(null)} onSent={sent} />}
    </>
  );
}
