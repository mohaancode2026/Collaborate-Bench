import { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, Consultant } from '../api';
import { useAuth } from '../auth';
import { ConsultantDraft, ConsultantForm, toDraft, toInput } from '../components';
import { ago, Avatar, copyText, Empty, FieldErrors, Modal, Spinner, useConfirm, useErrorHandler, useToast } from '../ui';

type LeadRow = {
  consultant: Consultant; lastActivity?: string | null; lastActivityUtc?: string | null; lastActivityBy?: string | null;
  nextReminderUtc?: string | null; mapped: number; rejected: number;
};
type Reminder = { id: number; consultantId: number; consultantName: string; linkedInUrl?: string | null; dueUtc: string; note?: string | null; status: string; overdue: boolean; byName: string; mine: boolean };
type KeyResponse = { token: string; expiresUtc: string };

const when = (iso: string) => new Date(iso).toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

/** Leads sourced from LinkedIn with the browser extension: follow up, move to the bench, and connect the extension. */
export default function LinkedInLeads() {
  const { scope, can } = useAuth();
  const nav = useNavigate();
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [rows, setRows] = useState<LeadRow[] | null>(null);
  const [reminders, setReminders] = useState<Reminder[]>([]);
  const [q, setQ] = useState('');
  const [move, setMove] = useState<{ id: number; draft: ConsultantDraft } | null>(null);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const [key, setKey] = useState<KeyResponse | null>(null);

  const load = useCallback(() => {
    api<LeadRow[]>('/linkedin/leads', { query: { scope } }).then(setRows).catch(e => { setRows([]); onError(e); });
    api<Reminder[]>('/linkedin/reminders').then(setReminders).catch(onError);
  }, [scope, onError]);
  useEffect(load, [load]);

  const shown = (rows ?? []).filter(r => !q || `${r.consultant.name} ${r.consultant.title} ${r.consultant.location} ${r.consultant.ownerName} ${r.consultant.skills.join(' ')}`.toLowerCase().includes(q.toLowerCase()));

  const closeReminder = async (r: Reminder, status: 'Done' | 'Dismissed') => {
    try { await api(`/linkedin/reminders/${r.id}`, { method: 'PATCH', body: { status } }); setReminders(x => x.filter(y => y.id !== r.id)); load(); }
    catch (e) { onError(e); }
  };

  // Moving to the bench needs what a lead usually lacks (pay rate, visa, contact details), so it goes through the consultant form.
  const startMove = (c: Consultant) => { setErrors({}); setMove({ id: c.id, draft: { ...toDraft(c), status: 'Bench', rate: c.rate || '' } }); };
  const saveMove = async () => {
    if (!move) return;
    setBusy(true); setErrors({});
    try {
      await api(`/consultants/${move.id}`, { method: 'PUT', body: toInput(move.draft) });
      notify('success', 'Moved to the bench', `${move.draft.name} is now on the bench and in matching.`);
      setMove(null); load();
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };

  const remove = async (c: Consultant) => {
    if (!await confirm({ title: 'Remove lead?', message: `${c.name} will be removed. Their activity history is kept.`, okLabel: 'Remove', danger: true })) return;
    try { await api(`/consultants/${c.id}`, { method: 'DELETE' }); notify('success', 'Lead removed', c.name); load(); } catch (e) { onError(e); }
  };

  const createKey = async () => {
    try { setKey(await api<KeyResponse>('/auth/extension-key', { method: 'POST' })); } catch (e) { onError(e); }
  };

  return (
    <>
      <div className="ph"><div><h1>LinkedIn Leads</h1><p>Candidates saved from LinkedIn with the Collaborate-Bench side panel. Leads stay off the bench until you move them there.</p></div>
        <div className="ph-act"><button className="btn btn-dark" onClick={createKey}>Connect the extension</button></div></div>

      {reminders.length > 0 && (
        <div className="card">
          <div className="card-h"><h3>Your reminders ({reminders.length})</h3></div>
          <div className="tw"><table><tbody>
            {reminders.map(r => (
              <tr key={r.id}>
                <td><b>{r.consultantName}</b>{r.note && <><br /><small className="muted">{r.note}</small></>}</td>
                <td style={{ whiteSpace: 'nowrap' }}><span className={`tag ${r.overdue ? 'r' : 'y'}`}>{r.overdue ? 'Overdue' : 'Due'} {when(r.dueUtc)}</span></td>
                <td style={{ whiteSpace: 'nowrap', textAlign: 'right' }}>
                  {r.linkedInUrl && <a className="btn btn-sm btn-outline" href={r.linkedInUrl} target="_blank" rel="noreferrer">LinkedIn</a>}{' '}
                  <button className="btn btn-sm btn-outline" onClick={() => closeReminder(r, 'Done')}>Done</button>{' '}
                  <button className="btn btn-sm btn-outline" onClick={() => closeReminder(r, 'Dismissed')}>Dismiss</button>
                </td>
              </tr>))}
          </tbody></table></div>
        </div>)}

      <div className="card">
        <div className="card-h"><h3>Leads ({rows?.length ?? 0})</h3>
          <input className="inp" placeholder="Filter by name, skill, location, recruiter…" style={{ maxWidth: 300 }} value={q} onChange={e => setQ(e.target.value)} /></div>
        {rows === null ? <Empty><Spinner dark /> Loading…</Empty> : (
          <div className="tw"><table><thead><tr><th>Candidate</th><th>Skills</th><th>Location</th><th>Jobs</th><th>Last touch</th><th>Next reminder</th><th></th></tr></thead><tbody>
            {shown.map(({ consultant: c, ...r }) => (
              <tr key={c.id}>
                <td><div className="who"><Avatar name={c.name} /><div><b>{c.name}</b><small>{c.title || '—'}</small><small style={{ display: 'block' }}>saved {c.daysOnBench === 0 ? 'today' : `${c.daysOnBench}d ago`} by {c.ownerName}</small></div></div></td>
                <td style={{ minWidth: 160 }}>{c.skills.slice(0, 3).map(s => <span key={s} className="tag">{s}</span>)}{c.skills.length > 3 && <span className="tag s">+{c.skills.length - 3}</span>}</td>
                <td style={{ minWidth: 120 }}>{c.location || '—'}</td>
                <td style={{ whiteSpace: 'nowrap' }}>{r.mapped > 0 && <span className="tag g">{r.mapped} mapped</span>} {r.rejected > 0 && <span className="tag r">{r.rejected} rejected</span>}{!r.mapped && !r.rejected && <span className="muted">—</span>}</td>
                <td style={{ minWidth: 170 }}>{r.lastActivity ? <>{r.lastActivity}<br /><small className="muted">{r.lastActivityBy} · {ago(r.lastActivityUtc!)}</small></> : <span className="muted">No touches yet</span>}</td>
                <td style={{ whiteSpace: 'nowrap' }}>{r.nextReminderUtc ? <span className={`tag ${new Date(r.nextReminderUtc) < new Date() ? 'r' : 'y'}`}>{when(r.nextReminderUtc)}</span> : <span className="muted">—</span>}</td>
                <td><div style={{ display: 'grid', gap: 4, whiteSpace: 'nowrap' }}>
                  {can('bench.edit') && <button className="btn btn-sm btn-primary" onClick={() => startMove(c)}>Move to bench</button>}
                  {c.linkedInUrl && <a className="btn btn-sm btn-outline" href={c.linkedInUrl} target="_blank" rel="noreferrer">LinkedIn profile</a>}
                  {can('bench.delete') && <button className="btn btn-sm btn-outline" style={{ color: 'var(--bad)' }} onClick={() => remove(c)}>Remove</button>}
                </div></td>
              </tr>))}
          </tbody></table>
          {shown.length === 0 && <Empty>{rows.length === 0
            ? <>No leads yet. Install the side panel, open a LinkedIn profile and press <b>Save as lead</b>.</>
            : 'No leads match this filter.'}</Empty>}</div>)}
      </div>

      <div className="card">
        <div className="card-h"><h3>The LinkedIn side panel</h3></div>
        <div style={{ padding: '0 18px 18px', lineHeight: 1.6 }}>
          <p>Works next to LinkedIn in Chrome or Edge. On a <b>profile</b>:</p>
          <ul style={{ margin: '6px 0 10px 18px' }}>
            <li>see whether the person is already in Collaborate-Bench</li>
            <li>save them as a lead</li>
            <li>map them to a requirement</li>
            <li>log messages and calls</li>
            <li>set reminders</li>
            <li>see matching jobs</li>
          </ul>
          <p>On a <b>job post</b>, save it as a requirement and see which bench consultants match.</p>
          <p className="mt">It reads only the page you have open, and nothing is saved until you click. It doesn't crawl LinkedIn or send messages for you.</p>
          <ol style={{ margin: '10px 0 0 18px' }}>
            <li>Open <code>chrome://extensions</code>, turn on <b>Developer mode</b>, choose <b>Load unpacked</b> and pick the <code>extension</code> folder from the Collaborate-Bench repository (or install it from your company's extension store).</li>
            <li>Click the Collaborate-Bench icon to open the panel. Enter this server address: <code>{location.origin}</code></li>
            <li>Sign in with your email and password. If you sign in with Microsoft, press <b>Connect the extension</b> above and paste the key instead.</li>
          </ol>
          <p className="mt"><button className="btn btn-outline btn-sm" onClick={() => nav('/bench')}>Go to the bench</button></p>
        </div>
      </div>

      {move && (
        <Modal title={`Move ${move.draft.name} to the bench`} onClose={() => setMove(null)}
          footer={<><button className="btn btn-outline" onClick={() => setMove(null)}>Cancel</button>
            <button className="btn btn-primary" onClick={saveMove} disabled={busy}>{busy ? <Spinner /> : 'Move to bench'}</button></>}>
          <p className="muted" style={{ marginBottom: 10 }}>Add what the profile didn't have, usually pay rate, visa and contact details. Then they're on the bench, in matching and available to the Autopilot.</p>
          <ConsultantForm draft={move.draft} set={d => setMove({ ...move, draft: d })} errors={errors} />
        </Modal>)}

      {key && (
        <Modal title="Connection key for the extension" onClose={() => setKey(null)}
          footer={<><button className="btn btn-outline" onClick={() => setKey(null)}>Close</button><button className="btn btn-primary" onClick={() => copyText(key.token, notify)}>Copy key</button></>}>
          <p>In the side panel, open <b>Sign in with Microsoft? Use a connection key</b>, paste this key and press <b>Connect</b>.</p>
          <p className="muted mt">Server address: <code>{location.origin}</code> · valid until {when(key.expiresUtc)}. Treat it like a password: anyone with the key can act as you until it expires.</p>
          <textarea className="inp mono mt" readOnly rows={5} value={key.token} onFocus={e => e.currentTarget.select()} style={{ fontSize: 11.5 }} />
        </Modal>)}
    </>
  );
}
