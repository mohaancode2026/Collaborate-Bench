import { useEffect, useState } from 'react';
import { api, Assignee } from './api';
import { useAuth } from './auth';
import { roleLabel } from './roles';
import { Empty, Modal, Spinner, useErrorHandler, useToast } from './ui';

/**
 * Assign consultants. Admin / Delivery Manager / Manager (bench.assign): to a Team Lead, who then gives them to a recruiter.
 * A Team Lead (bench.reassign): to one of their recruiters.
 */
export function AssignDialog({ ids, names, currentOwnerId, onClose, onDone }: {
  ids: number[]; names: string[]; currentOwnerId?: number; onClose: () => void; onDone: () => void;
}) {
  const { can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const [people, setPeople] = useState<Assignee[] | null>(null);
  const [pick, setPick] = useState<number | null>(null);
  const [showRecruiters, setShowRecruiters] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notifyThem, setNotifyThem] = useState(true);
  const [attach, setAttach] = useState(true);
  const [note, setNote] = useState('');
  const assigner = can('bench.assign');
  useEffect(() => { api<Assignee[]>('/consultants/assignees').then(setPeople).catch(e => { setPeople([]); onError(e); }); }, [onError]);

  const leads = (people ?? []).filter(p => p.role === 'Team Lead');
  const recruiters = (people ?? []).filter(p => p.role !== 'Team Lead');
  // Assigners see Team Leads first (recruiters on request); a Team Lead sees their recruiters (and themselves).
  const list = assigner ? (showRecruiters ? [...leads, ...recruiters] : leads) : people ?? [];

  const save = async () => {
    if (!pick) return;
    setBusy(true);
    try {
      const r = await api<{ assigned: number; ownerName: string; ownerRole: string; emailed: boolean; mailError?: string | null; resumesAttached: number }>(
        '/consultants/assign', { method: 'POST', body: { consultantIds: ids, ownerUserId: pick, notify: notifyThem, attachResumes: attach, note: note.trim() || undefined } });
      const who = `${r.assigned === 1 ? names[0] : `${r.assigned} consultants`} ${r.assigned === 1 ? 'is' : 'are'} now with ${r.ownerName} (${roleLabel(r.ownerRole)}).`;
      if (r.mailError) notify('warning', `Assigned to ${r.ownerName}, but the email didn't go`, `${who} ${r.mailError}`);
      else notify('success', `Assigned to ${r.ownerName}`, `${who}${r.emailed ? ` Emailed them${r.resumesAttached ? ` with ${r.resumesAttached} resume${r.resumesAttached === 1 ? '' : 's'}` : ''}.` : ''}`);
      onDone();
    } catch (e) { onError(e); } finally { setBusy(false); }
  };

  return (
    <Modal title={ids.length === 1 ? `Assign ${names[0]}` : `Assign ${ids.length} consultants`} onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button>
        <button className="btn btn-primary" onClick={save} disabled={!pick || busy}>{busy ? <Spinner /> : 'Assign'}</button></>}>
      <p className="muted" style={{ marginTop: 0 }}>{assigner
        ? 'Give them to a Team Lead. The Team Lead sees them on their bench and assigns each one to a Bench Sales Executive.'
        : 'Give them to a Bench Sales Executive in your team, who then markets them and matches them to jobs.'}</p>
      {people === null ? <Empty><Spinner dark /> Loading…</Empty> : list.length === 0 ? <Empty>{assigner ? 'No active Team Leads.' : 'No Bench Sales Executives in your team yet.'}</Empty> : (
        <div className="assign-list">
          {list.map(p => (
            <label key={p.id} className={`assign-row${pick === p.id ? ' on' : ''}${p.id === currentOwnerId ? ' cur' : ''}`}>
              <input type="radio" name="assignee" checked={pick === p.id} onChange={() => setPick(p.id)} />
              <span style={{ flex: 1 }}><b>{p.name}</b> <small className="muted">{roleLabel(p.role)}{p.team ? ` · ${p.team}` : ''}</small>
                {p.id === currentOwnerId && <span className="tag s">current</span>}</span>
              <small className="muted">{p.consultants} on bench</small>
            </label>))}
        </div>)}
      {pick && (people ?? []).find(p => p.id === pick) && (() => {
        const to = (people ?? []).find(p => p.id === pick)!;
        return (
          <div className="assign-mail">
            <label className="check" style={{ border: 0, padding: '4px 0' }}><input type="checkbox" checked={notifyThem} onChange={e => setNotifyThem(e.target.checked)} />
              <span>Email {to.name} from my Outlook</span></label>
            {notifyThem && <>
              <label className="check" style={{ border: 0, padding: '4px 0' }}><input type="checkbox" checked={attach} onChange={e => setAttach(e.target.checked)} />
                <span>Attach their latest resume{ids.length === 1 ? '' : 's'}</span></label>
              <textarea className="inp" style={{ minHeight: 60, marginTop: 4 }} value={note} onChange={e => setNote(e.target.value)} maxLength={1000}
                placeholder={to.role === 'Team Lead' ? 'Optional note, e.g. "Please give these to your executives today."' : 'Optional note, e.g. "Start marketing this week."'} />
              <small className="muted">The email lists each consultant with a link to their profile, and adds your signature.</small>
            </>}
          </div>);
      })()}
      {assigner && recruiters.length > 0 && !showRecruiters && <button className="linkish" style={{ marginTop: 10 }} onClick={() => setShowRecruiters(true)}>Assign straight to a Bench Sales Executive instead…</button>}
    </Modal>
  );
}
