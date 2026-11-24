import { useEffect, useMemo, useState } from 'react';
import { api } from './api';
import { useAuth } from './auth';
import { roleLabel } from './roles';
import { ComposeDialog } from './compose';
import { Alert, copyText, Empty, Field, FieldErrors, Modal, Spinner, useErrorHandler, useToast, visaClass } from './ui';

export interface HotlistConsultant {
  id: number; name: string; title: string; exp: number; visa: string; location: string; rate: number; status: string;
  skills: string[]; ownerName: string; ownerTeam?: string | null; hasResume: boolean;
}
interface Hotlist { text: string; subject: string; body: string; count: number; consultants: HotlistConsultant[]; }
interface Recipient { id: number; name: string; email: string; role: string; team?: string | null; }
interface SendResult { consultants: number; resumesAttached: number; emails: number; sentTo: string[]; noResume: string[]; tooLarge: string[]; }

/** Same layout as the server's hotlist, for the consultants picked here (copy / email to vendors). */
function hotlistText(list: HotlistConsultant[]) {
  const date = new Date().toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  const subject = `Daily Hotlist – ${date} | ${list.length} Consultant${list.length === 1 ? '' : 's'} Available (C2C/W2)`;
  const body = 'Hi Partner,\n\nPlease find our available consultants below. Reply with matching requirements and we\'ll share resumes with RTR.\n\n' +
    list.map((c, i) => `${i + 1}. ${c.name} – ${c.title}\n   Exp: ${c.exp}+ yrs | Visa: ${c.visa} | Location: ${c.location} | Rate: $${Math.round(c.rate)}/hr C2C\n   Skills: ${c.skills.join(', ')}`).join('\n\n');
  return { subject, body };
}

/**
 * The hotlist: pick consultants, then send it with their latest resumes attached to the Team Leads (or anyone in the company),
 * from your Outlook. Also: copy it, or open it in the email window for vendors.
 */
export function HotlistDialog({ onClose }: { onClose: () => void }) {
  const { can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const [h, setH] = useState<Hotlist | null>(null);
  const [people, setPeople] = useState<Recipient[]>([]);
  const [picked, setPicked] = useState<Set<number>>(new Set());
  const [to, setTo] = useState<Set<number>>(new Set());
  const [q, setQ] = useState('');
  const [note, setNote] = useState('');
  const [attach, setAttach] = useState(true);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [vendorMail, setVendorMail] = useState<{ subject: string; body: string } | null>(null);
  const [showAllPeople, setShowAllPeople] = useState(false);

  useEffect(() => {
    api<Hotlist>('/consultants/hotlist').then(r => { setH(r); setPicked(new Set(r.consultants.map(c => c.id))); }).catch(e => { onError(e); onClose(); });
    api<Recipient[]>('/consultants/hotlist/recipients').then(r => { setPeople(r); setTo(new Set(r.filter(p => p.role === 'Team Lead').map(p => p.id))); }).catch(() => setPeople([]));
  }, [onError, onClose]);

  const shown = useMemo(() => (h?.consultants ?? []).filter(c => !q || `${c.name} ${c.title} ${c.visa} ${c.location} ${c.skills.join(' ')} ${c.ownerName}`.toLowerCase().includes(q.toLowerCase())), [h, q]);
  const chosen = (h?.consultants ?? []).filter(c => picked.has(c.id));
  const leads = people.filter(p => p.role === 'Team Lead');
  const others = people.filter(p => p.role !== 'Team Lead');
  const toggle = (set: Set<number>, id: number, apply: (s: Set<number>) => void) => { const n = new Set(set); if (n.has(id)) n.delete(id); else n.add(id); apply(n); };
  const allShownPicked = shown.length > 0 && shown.every(c => picked.has(c.id));
  const withoutResume = chosen.filter(c => !c.hasResume).length;

  const send = async () => {
    setBusy(true); setErrors({});
    try {
      const r = await api<SendResult>('/consultants/hotlist/send', { method: 'POST', body: { consultantIds: [...picked], recipientUserIds: [...to], note: note.trim() || undefined, attachResumes: attach } });
      const extra = [r.emails > 1 ? `split over ${r.emails} emails` : '', r.noResume.length ? `no resume yet: ${r.noResume.join(', ')}` : '', r.tooLarge.length ? `too large: ${r.tooLarge.join(', ')}` : ''].filter(Boolean).join(' · ');
      notify(r.noResume.length || r.tooLarge.length ? 'warning' : 'success', `Hotlist sent to ${r.sentTo.length} ${r.sentTo.length === 1 ? 'person' : 'people'}`,
        `${r.consultants} consultants, ${r.resumesAttached} resume${r.resumesAttached === 1 ? '' : 's'} attached${extra ? ` · ${extra}` : ''}.`);
      onClose();
    } catch (e) { onError(e, setErrors); } finally { setBusy(false); }
  };

  if (vendorMail) return <ComposeDialog title="Send hotlist to vendors" initial={vendorMail} templateCategory={undefined} onClose={() => setVendorMail(null)} onSent={onClose} />;

  return (
    <Modal wide title={h ? `Hotlist · ${picked.size} of ${h.count} consultants` : 'Hotlist'} onClose={onClose}
      footer={<>
        <button className="btn btn-outline" style={{ marginRight: 'auto' }} disabled={!chosen.length} onClick={() => copyText(`Subject: ${hotlistText(chosen).subject}\n\n${hotlistText(chosen).body}`, notify)}>Copy</button>
        {can('mail.view') && <button className="btn btn-outline" disabled={!chosen.length} onClick={() => setVendorMail(hotlistText(chosen))}>Email to vendors…</button>}
        <button className="btn btn-primary" onClick={send} disabled={busy || !picked.size || !to.size}>
          {busy ? <><Spinner /> Sending…</> : `Send to ${to.size} ${to.size === 1 ? 'person' : 'people'}${attach ? ' with resumes' : ''}`}</button></>}>
      {!h ? <Empty><Spinner dark /> Loading the bench…</Empty> : (
        <div className="hot-grid">
          <div>
            <div className="hot-h">
              <label className="check" style={{ border: 0, padding: 0 }}><input type="checkbox" checked={allShownPicked} onChange={e => {
                const n = new Set(picked); shown.forEach(c => (e.target.checked ? n.add(c.id) : n.delete(c.id))); setPicked(n);
              }} /><span><b>Consultants</b> ({picked.size} picked)</span></label>
              <input className="inp" placeholder="Filter by skill, visa, name…" style={{ maxWidth: 220 }} value={q} onChange={e => setQ(e.target.value)} />
            </div>
            {errors.consultantIds && <p style={{ color: 'var(--bad)', fontSize: 12.5, margin: '0 0 6px' }}>{errors.consultantIds[0]}</p>}
            <div className="hot-list">
              {shown.map(c => (
                <label key={c.id} className={`hot-row${picked.has(c.id) ? ' on' : ''}`}>
                  <input type="checkbox" checked={picked.has(c.id)} onChange={() => toggle(picked, c.id, setPicked)} />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div><b>{c.name}</b> <small className="muted">{c.title} · {c.exp}+ yrs</small></div>
                    <div style={{ fontSize: 12.5 }}><span className={`tag ${visaClass(c.visa)}`}>{c.visa || '—'}</span>{c.location} · ${Math.round(c.rate)}/hr · <span className="muted">{c.ownerName}</span></div>
                    <div>{c.skills.slice(0, 5).map(s => <span key={s} className="tag s">{s}</span>)}</div>
                  </div>
                  <span className={`tag ${c.hasResume ? 'g' : 'y'}`} title={c.hasResume ? 'Latest resume will be attached' : 'No resume on file'}>{c.hasResume ? '📄 Resume' : 'No resume'}</span>
                </label>))}
              {shown.length === 0 && <Empty>No consultant matches that filter.</Empty>}
            </div>
          </div>
          <div>
            <Field label={<b>Send to</b>} error={errors.recipientUserIds}>
              <div className="hot-people">
                {leads.length === 0 && <p className="muted" style={{ fontSize: 13 }}>No active Team Leads.</p>}
                {leads.map(p => <Person key={p.id} p={p} on={to.has(p.id)} toggle={() => toggle(to, p.id, setTo)} />)}
                {others.length > 0 && (showAllPeople
                  ? others.map(p => <Person key={p.id} p={p} on={to.has(p.id)} toggle={() => toggle(to, p.id, setTo)} />)
                  : <button className="linkish" style={{ marginTop: 6 }} onClick={() => setShowAllPeople(true)}>+ Add someone else ({others.length})</button>)}
              </div>
            </Field>
            <Field label="Message (optional)" error={errors.note}>
              <textarea className="inp" style={{ minHeight: 80 }} value={note} onChange={e => setNote(e.target.value)} maxLength={2000}
                placeholder="Here is today's hotlist. Please market these consultants to your vendors; their latest resumes are attached." />
            </Field>
            <label className="check"><input type="checkbox" checked={attach} onChange={e => setAttach(e.target.checked)} /><span>Attach each consultant's latest resume</span></label>
            {attach && withoutResume > 0 && <Alert type="warning" title={`${withoutResume} picked consultant${withoutResume === 1 ? " has" : 's have'} no resume`}>They're listed in the email as "no resume on file yet".</Alert>}
            <p className="muted" style={{ fontSize: 12.5 }}>Sent from your Outlook with your signature. Large sets of resumes go out as several emails ("part 2 of 3").</p>
          </div>
        </div>)}
    </Modal>
  );
}

const Person = ({ p, on, toggle }: { p: Recipient; on: boolean; toggle: () => void }) => (
  <label className="check" style={{ padding: '7px 0' }}>
    <input type="checkbox" checked={on} onChange={toggle} />
    <span><b>{p.name}</b> <small className="muted">{roleLabel(p.role)}{p.team ? ` · ${p.team}` : ''} · {p.email}</small></span>
  </label>
);
