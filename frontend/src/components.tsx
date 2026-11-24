import { useEffect, useState } from 'react';
import { api, Consultant, Requirement, Submission, TeamMember, VISAS } from './api';
import { useAuth } from './auth';
import { Alert, Avatar, Field, FieldErrors, Modal, Spinner, useErrorHandler, useToast } from './ui';

export interface ConsultantDraft {
  id?: number;
  name: string; title: string; email: string; phone: string; visa: string; location: string; relocate: boolean;
  rate: number | ''; exp: number | ''; status: string; skills: string; summary: string; ownerId?: number; rowVersion?: string;
  // details vendors ask for on a submission
  linkedInUrl: string; visaValidUntil: string; availability: string; preferredLocations: string; usExp: number | '';
  education: string; certifications: string;
  /** A new passport number typed in the form ('' keeps the one on file). */
  passportNumber: string;
  /** Masked number already on file, e.g. ••••1234 (display only). */
  passportOnFile?: string | null;
  clearPassport?: boolean;
}
const noDetails = { linkedInUrl: '', visaValidUntil: '', availability: '', preferredLocations: '', usExp: '' as const, education: '', certifications: '', passportNumber: '' };
export const emptyDraft = (): ConsultantDraft => ({ name: '', title: '', email: '', phone: '', visa: '', location: '', relocate: false, rate: '', exp: '', status: 'Bench', skills: '', summary: '', ...noDetails });
export const toDraft = (c: Consultant): ConsultantDraft => ({
  ...c, ...noDetails, skills: c.skills.join(', '), ownerId: c.ownerId, rowVersion: c.rowVersion,
  linkedInUrl: c.linkedInUrl ?? '', visaValidUntil: c.visaValidUntil ? c.visaValidUntil.slice(0, 10) : '', availability: c.availability ?? '',
  preferredLocations: c.preferredLocations ?? '', usExp: c.usExp ?? '', education: c.education ?? '', certifications: c.certifications ?? '',
  passportOnFile: c.passportOnFile ?? null,
});
export const toInput = (d: ConsultantDraft) => ({
  ...d, rate: Number(d.rate) || 0, exp: Number(d.exp) || 0, skills: d.skills.split(',').map(s => s.trim()).filter(Boolean),
  usExp: d.usExp === '' ? null : Number(d.usExp), visaValidUntil: d.visaValidUntil || null,
  passportNumber: d.passportNumber.trim() || null, clearPassport: !!d.clearPassport, passportOnFile: undefined, id: undefined,
});

const AVAILABILITY = ['Immediately', '1 week notice', '2 weeks notice', '30 days notice', 'On project, ending soon'];

/** Consultant fields with inline validation messages coming from the API. */
export function ConsultantForm({ draft, set, errors }: { draft: ConsultantDraft; set: (d: ConsultantDraft) => void; errors: FieldErrors }) {
  const { can } = useAuth();
  const [owners, setOwners] = useState<TeamMember[]>([]);
  const canReassign = can('bench.reassign');
  useEffect(() => { if (canReassign) api<TeamMember[]>('/users').then(u => setOwners(u.filter(x => x.active && x.role !== 'Admin'))).catch(() => {}); }, [canReassign]);
  const f = <K extends keyof ConsultantDraft>(k: K) => (v: ConsultantDraft[K]) => set({ ...draft, [k]: v });
  const notify = useToast();
  const onError = useErrorHandler();
  const [revealed, setRevealed] = useState<string | null>(null);
  const reveal = async () => {
    if (!draft.id) return;
    try { setRevealed((await api<{ passportNumber: string }>(`/consultants/${draft.id}/passport`)).passportNumber); notify('info', 'Passport number shown', 'This view is recorded in the activity log.'); }
    catch (e) { onError(e); }
  };
  return (
    <>
      <div className="row2">
        <Field label="Full name *" error={errors.name}><input className="inp" value={draft.name} onChange={e => f('name')(e.target.value)} /></Field>
        <Field label="Title" error={errors.title}><input className="inp" value={draft.title} onChange={e => f('title')(e.target.value)} /></Field>
      </div>
      <div className="row2">
        <Field label={draft.status === 'Lead' ? 'Email' : 'Email *'} error={errors.email}><input className="inp" type="email" value={draft.email} onChange={e => f('email')(e.target.value)} /></Field>
        <Field label={draft.status === 'Lead' ? 'Phone' : 'Phone *'} error={errors.phone}><input className="inp" value={draft.phone} onChange={e => f('phone')(e.target.value)} placeholder="+1 (469) 555-0100" /></Field>
      </div>
      <div className="row3">
        <Field label="Visa" error={errors.visa}><select className="inp" value={draft.visa} onChange={e => f('visa')(e.target.value)}><option value="">—</option>{VISAS.map(v => <option key={v}>{v}</option>)}</select></Field>
        <Field label="Pay rate ($/hr)" error={errors.rate}><input className="inp" type="number" placeholder="Optional" value={draft.rate} onChange={e => f('rate')(e.target.value === '' ? '' : Number(e.target.value))} /></Field>
        <Field label="Experience (yrs)" error={errors.exp}><input className="inp" type="number" value={draft.exp} onChange={e => f('exp')(e.target.value === '' ? '' : Number(e.target.value))} /></Field>
      </div>
      <div className="row2">
        <Field label="Location" error={errors.location}><input className="inp" value={draft.location} placeholder="City, ST or Remote" onChange={e => f('location')(e.target.value)} /></Field>
        <Field label={<>Owner {!canReassign && '🔒'}</>} error={errors.ownerId}>
          {canReassign
            ? <select className="inp" value={draft.ownerId ?? ''} onChange={e => f('ownerId')(e.target.value ? Number(e.target.value) : undefined)}>
                <option value="">Me</option>{owners.map(o => <option key={o.id} value={o.id}>{o.name} · {o.role}</option>)}</select>
            : <input className="inp" disabled value="You (Team Leads can reassign)" title="Only Team Leads and above can reassign consultants" />}
        </Field>
      </div>
      <Field label="Skills * (comma separated)" error={errors.skills}><input className="inp" value={draft.skills} onChange={e => f('skills')(e.target.value)} /></Field>
      <Field label="Hotlist summary" error={errors.summary}><textarea className="inp" style={{ minHeight: 70 }} value={draft.summary} onChange={e => f('summary')(e.target.value)} /></Field>
      <div className="form-sec">Vendor submission details</div>
      <div className="row2">
        <Field label="LinkedIn profile" error={errors.linkedInUrl}><input className="inp" value={draft.linkedInUrl} placeholder="https://www.linkedin.com/in/name" onChange={e => f('linkedInUrl')(e.target.value)} /></Field>
        <Field label={<span className="flex between" style={{ width: '100%' }}>Passport number
          {draft.passportOnFile && !draft.clearPassport && <span className="muted" style={{ fontWeight: 500 }}>On file: {revealed ?? draft.passportOnFile}{' '}
            {draft.id && can('bench.edit') && !revealed && <a href="#" className="lnk" onClick={e => { e.preventDefault(); reveal(); }}>Show</a>}{' '}
            <a href="#" className="lnk" onClick={e => { e.preventDefault(); set({ ...draft, clearPassport: true, passportNumber: '' }); }}>Remove</a></span>}
          {draft.clearPassport && <span className="muted" style={{ fontWeight: 500 }}>Will be removed · <a href="#" className="lnk" onClick={e => { e.preventDefault(); set({ ...draft, clearPassport: false }); }}>Undo</a></span>}</span>}
          error={errors.passportNumber}>
          <input className="inp" value={draft.passportNumber} autoComplete="off" spellCheck={false}
            placeholder={draft.passportOnFile && !draft.clearPassport ? 'Type to replace the number on file' : 'e.g. Z1234567'}
            onChange={e => set({ ...draft, passportNumber: e.target.value.toUpperCase(), clearPassport: false })} />
        </Field>
      </div>
      <div className="row3">
        <Field label="Visa valid until" error={errors.visaValidUntil}><input className="inp" type="date" value={draft.visaValidUntil} onChange={e => f('visaValidUntil')(e.target.value)} /></Field>
        <Field label="Availability" error={errors.availability}>
          <input className="inp" list="cb-availability" value={draft.availability} placeholder="Immediately" onChange={e => f('availability')(e.target.value)} />
          <datalist id="cb-availability">{AVAILABILITY.map(a => <option key={a} value={a} />)}</datalist>
        </Field>
        <Field label="US experience (yrs)" error={errors.usExp}><input className="inp" type="number" min={0} value={draft.usExp} onChange={e => f('usExp')(e.target.value === '' ? '' : Number(e.target.value))} /></Field>
      </div>
      <div className="row2">
        <Field label="Preferred locations" error={errors.preferredLocations}><input className="inp" value={draft.preferredLocations} placeholder="e.g. Dallas, TX; Remote; anywhere in the US" onChange={e => f('preferredLocations')(e.target.value)} /></Field>
        <Field label="Highest education" error={errors.education}><input className="inp" value={draft.education} placeholder="e.g. MS Computer Science, UNT, 2014" onChange={e => f('education')(e.target.value)} /></Field>
      </div>
      <Field label="Certifications" error={errors.certifications}><input className="inp" value={draft.certifications} placeholder="e.g. AWS Solutions Architect – Associate; CKA" onChange={e => f('certifications')(e.target.value)} /></Field>
      <div className="row2">
        <Field label="Status" error={errors.status}><select className="inp" value={draft.status} onChange={e => f('status')(e.target.value)}>{['Bench', 'Marketing', 'Placed'].map(s => <option key={s}>{s}</option>)}</select></Field>
        <label className="chip" style={{ alignSelf: 'end', marginBottom: 14 }}><input type="checkbox" checked={draft.relocate} onChange={e => f('relocate')(e.target.checked)} /> Open to relocate</label>
      </div>
    </>
  );
}

/** Suggested submit rate: about 12% over pay, never above 95% of the bill rate. Always between pay and the bill rate:
 * the pay rate itself when bill equals pay (no margin), '' when bill is below pay, so nothing invalid is pre-filled. */
export const suggest = (pay: number, bill: number): number | '' => {
  if (!(pay > 0) || !(bill >= pay)) return '';
  if (bill === pay) return pay;
  const floor = Math.ceil(pay * 1.1), ceil = Math.floor(bill * 0.95);
  const s = Math.max(Math.min(ceil, Math.round(pay * 1.12)), Math.min(floor, ceil));
  if (s > pay && s <= bill) return s;
  const mid = Math.floor((pay + bill) / 2);
  return mid > pay && mid <= bill ? mid : bill;
};

/** Shown instead of the usual rate checks when no submit rate can work (bill below pay). */
export const noMargin = (pay: number, bill: number, fromPosting = false) => pay > 0 && bill > 0 && bill < pay
  ? fromPosting
    ? `The posted $${bill}/hr is below the $${pay}/hr pay rate. Job boards show what the employer pays, not the vendor's bill rate: enter the vendor's bill rate.`
    : `The $${bill}/hr bill rate is below the $${pay}/hr pay rate. Ask the vendor for more or agree a lower pay rate.`
  : '';

/**
 * One line about the rates, the same rules as the server: pay ≤ submit ≤ bill. Submitting at the pay rate is allowed
 * (it's flagged as no margin). kind: 'error' blocks submitting, 'warn' doesn't, 'ok' is the margin.
 */
export function rateCheck(pay: number, submit: number, bill: number, opts: { fromPosting?: boolean; showMargin?: boolean } = {}):
  { kind: 'error' | 'warn' | 'ok'; text: string } | null {
  const nm = noMargin(pay, bill, opts.fromPosting);
  if (nm) return { kind: 'error', text: nm };
  if (bill > 0 && submit > bill) return { kind: 'error', text: `Above the $${bill}/hr bill rate` };
  if (submit > 0 && pay > 0 && submit < pay) return { kind: 'error', text: `Below the $${pay}/hr pay rate` };
  if (submit > 0 && pay > 0 && submit === pay) return { kind: 'warn', text: 'No margin: you are submitting at the consultant pay rate.' };
  if (opts.showMargin && submit > 0 && pay > 0) return { kind: 'ok', text: `Margin $${(submit - pay).toFixed(2)}/hr (${Math.round((submit - pay) / submit * 100)}%)` };
  return null;
}
export const rateColor = (k?: 'error' | 'warn' | 'ok') => (k === 'error' ? 'var(--bad)' : k === 'warn' ? 'var(--warn, #b45309)' : 'var(--muted)');

/** Submit a consultant to a requirement: RTR required, rate checks, AI rate advice. */
export function SubmitDialog({ consultantId, requirementId, onClose, onDone }: { consultantId: number; requirementId: number; onClose: () => void; onDone?: () => void }) {
  const { can } = useAuth();
  const notify = useToast();
  const onError = useErrorHandler();
  const [c, setC] = useState<Consultant | null>(null);
  const [r, setR] = useState<Requirement | null>(null);
  const [submitRate, setSubmitRate] = useState<number | ''>('');
  const [payRate, setPayRate] = useState<number | ''>('');
  const [notes, setNotes] = useState('');
  const [rtr, setRtr] = useState(false);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const [advice, setAdvice] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api<Consultant>(`/consultants/${consultantId}`), api<Requirement>(`/requirements/${requirementId}`)])
      .then(([cc, rr]) => { setC(cc); setR(rr); setPayRate(cc.rate); setSubmitRate(suggest(cc.rate, rr.rate)); })
      .catch(e => { onError(e); onClose(); });
  }, [consultantId, requirementId, onError, onClose]);

  const sr = Number(submitRate), pr = Number(payRate);
  const check = r ? rateCheck(pr, sr, r.rate, { showMargin: can('margin.view') }) : null;
  const rateMsg = check?.text ?? '';

  const aiRate = async () => {
    setAdvice('…');
    try {
      const a = await api<{ rate: number; why: string; source: string; notice?: string }>('/ai/rate-advice', { method: 'POST', body: { consultantId, requirementId } });
      setSubmitRate(a.rate); setAdvice(`${a.source === 'claude' ? 'Claude' : 'Built-in'}: ${a.why}`);
    } catch (e) { setAdvice(null); onError(e); }
  };

  const submit = async () => {
    setBusy(true); setErrors({});
    try {
      const res = await api<{ submission: Submission; warnings: string[] }>('/submissions', { method: 'POST', body: { consultantId, requirementId, submitRate: sr, payRate: pr, rtrSigned: rtr, notes } });
      notify('success', 'Submission created', `${res.submission.consultantName} → ${res.submission.title}`);
      res.warnings.forEach(w => notify('warning', 'Heads up', w));
      onDone?.(); onClose();
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };

  return (
    <Modal title="Submit consultant" onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={submit} disabled={busy || !c}>{busy ? <Spinner /> : 'Submit'}</button></>}>
      {!c || !r ? <div className="empty"><Spinner dark /> Loading…</div> : (
        <>
          <div className="flex" style={{ marginBottom: 14 }}><Avatar name={c.name} /><div><b>{c.name}</b><div className="muted" style={{ fontSize: 13 }}>→ {r.title} · {r.client} via {r.vendor} · ${r.rate}/hr {r.type}</div></div></div>
          {!(r.visas.includes('Any') || r.visas.includes(c.visa)) && <Alert type="warning" title="Visa may not be accepted">{c.visa || 'Unknown visa'} isn't in the client's list ({r.visas.join(', ')}).</Alert>}
          <div className="row2">
            <Field label="Submit rate to vendor ($/hr) *" error={errors.submitRate}><input className="inp" type="number" value={submitRate} onChange={e => setSubmitRate(e.target.value === '' ? '' : Number(e.target.value))} /></Field>
            <Field label="Consultant pay rate ($/hr)" error={errors.payRate}><input className="inp" type="number" value={payRate} onChange={e => setPayRate(e.target.value === '' ? '' : Number(e.target.value))} /></Field>
          </div>
          <div className="flex" style={{ margin: '-4px 0 12px' }}>
            <button className="btn btn-sm btn-outline" onClick={aiRate}>✦ AI rate advice</button>
            <span className="muted" style={{ fontSize: 12.5, color: check ? rateColor(check.kind) : undefined }}>{advice === '…' ? <Spinner dark /> : advice ?? rateMsg}</span>
          </div>
          <Field label="Notes for vendor" error={errors.notes}><textarea className="inp" style={{ minHeight: 70 }} value={notes} onChange={e => setNotes(e.target.value)} placeholder="Availability, interview slots…" /></Field>
          <Field error={errors.rtrSigned}>
            <label className="check"><input type="checkbox" checked={rtr} onChange={e => setRtr(e.target.checked)} />
              <span>{c.name} approved this submission and signed the <b>Right to Represent (RTR)</b> for {r.client} via {r.vendor} *</span></label>
          </Field>
        </>
      )}
    </Modal>
  );
}
