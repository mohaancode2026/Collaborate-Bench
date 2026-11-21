import { useEffect, useState } from 'react';
import { api, Consultant, Requirement, Submission, TeamMember, VISAS } from './api';
import { useAuth } from './auth';
import { Alert, Avatar, Field, FieldErrors, Modal, Spinner, useErrorHandler, useToast } from './ui';

export interface ConsultantDraft {
  name: string; title: string; email: string; phone: string; visa: string; location: string; relocate: boolean;
  rate: number | ''; exp: number | ''; status: string; skills: string; summary: string; ownerId?: number; rowVersion?: string;
}
export const emptyDraft = (): ConsultantDraft => ({ name: '', title: '', email: '', phone: '', visa: '', location: '', relocate: false, rate: '', exp: '', status: 'Bench', skills: '', summary: '' });
export const toDraft = (c: Consultant): ConsultantDraft => ({ ...c, skills: c.skills.join(', '), ownerId: c.ownerId, rowVersion: c.rowVersion });
export const toInput = (d: ConsultantDraft) => ({
  ...d, rate: Number(d.rate) || 0, exp: Number(d.exp) || 0, skills: d.skills.split(',').map(s => s.trim()).filter(Boolean),
});

/** Consultant fields with inline validation messages coming from the API. */
export function ConsultantForm({ draft, set, errors }: { draft: ConsultantDraft; set: (d: ConsultantDraft) => void; errors: FieldErrors }) {
  const { can } = useAuth();
  const [owners, setOwners] = useState<TeamMember[]>([]);
  const canReassign = can('bench.reassign');
  useEffect(() => { if (canReassign) api<TeamMember[]>('/users').then(u => setOwners(u.filter(x => x.active && x.role !== 'Admin'))).catch(() => {}); }, [canReassign]);
  const f = <K extends keyof ConsultantDraft>(k: K) => (v: ConsultantDraft[K]) => set({ ...draft, [k]: v });
  return (
    <>
      <div className="row2">
        <Field label="Full name *" error={errors.name}><input className="inp" value={draft.name} onChange={e => f('name')(e.target.value)} /></Field>
        <Field label="Title" error={errors.title}><input className="inp" value={draft.title} onChange={e => f('title')(e.target.value)} /></Field>
      </div>
      <div className="row2">
        <Field label="Email" error={errors.email}><input className="inp" type="email" value={draft.email} onChange={e => f('email')(e.target.value)} /></Field>
        <Field label="Phone" error={errors.phone}><input className="inp" value={draft.phone} onChange={e => f('phone')(e.target.value)} /></Field>
      </div>
      <div className="row3">
        <Field label="Visa" error={errors.visa}><select className="inp" value={draft.visa} onChange={e => f('visa')(e.target.value)}><option value="">—</option>{VISAS.map(v => <option key={v}>{v}</option>)}</select></Field>
        <Field label="Pay rate ($/hr) *" error={errors.rate}><input className="inp" type="number" value={draft.rate} onChange={e => f('rate')(e.target.value === '' ? '' : Number(e.target.value))} /></Field>
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
      <div className="row2">
        <Field label="Status" error={errors.status}><select className="inp" value={draft.status} onChange={e => f('status')(e.target.value)}>{['Bench', 'Marketing', 'Placed'].map(s => <option key={s}>{s}</option>)}</select></Field>
        <label className="chip" style={{ alignSelf: 'end', marginBottom: 14 }}><input type="checkbox" checked={draft.relocate} onChange={e => f('relocate')(e.target.checked)} /> Open to relocate</label>
      </div>
    </>
  );
}

/** Suggested submit rate: about 12% over pay, never above 95% of the bill rate. */
export const suggest = (pay: number, bill: number) => {
  const floor = Math.ceil(pay * 1.1), ceil = Math.floor(bill * 0.95);
  return Math.max(Math.min(ceil, Math.round((pay || bill * 0.8) * 1.12)), Math.min(floor, ceil));
};

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
  const rateMsg = !r ? '' : sr > r.rate ? `Above the $${r.rate}/hr bill rate` : sr && pr && sr <= pr ? 'Must be above the pay rate'
    : can('margin.view') && sr ? `Margin $${(sr - pr).toFixed(2)}/hr (${Math.round((sr - pr) / sr * 100)}%)` : '';

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
            <span className="muted" style={{ fontSize: 12.5, color: rateMsg.startsWith('Above') || rateMsg.startsWith('Must') ? 'var(--bad)' : undefined }}>{advice === '…' ? <Spinner dark /> : advice ?? rateMsg}</span>
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
