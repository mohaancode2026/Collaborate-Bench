import { useEffect, useState } from 'react';
import { api, ApiError, Consultant, ConsultantProfile, postForm } from './api';
import { useAuth } from './auth';
import { ConsultantDraft, ConsultantForm, emptyDraft, toDraft, toInput } from './components';
import { parseIntake } from './intake';
import { Alert, Field, FieldErrors, Modal, Spinner, useErrorHandler, useToast } from './ui';

export const DOC_KINDS: [string, string][] = [
  ['Passport', 'Passport'], ['DriversLicense', "Driver's license"], ['StateId', 'State ID'], ['EAD', 'EAD card'], ['I20', 'I-20'],
  ['Visa', 'Visa stamp'], ['I797', 'I-797 approval'], ['SSN', 'SSN card'], ['Degree', 'Degree / transcript'], ['Other', 'Other'],
];
export const docKindLabel = (k: string) => DOC_KINDS.find(x => x[0] === k)?.[1] ?? k;
const ID_TYPES = ["Driver's License", 'State ID', 'None'];

export type ProfileDraft = Record<'legalName' | 'marketingTechnology' | 'keywords' | 'googleVoice' | 'personalPhone' | 'dateOfBirth' | 'ssnLast4' | 'idType' |
  'bachelors' | 'masters' | 'locationConstraints' | 'currentProjectEnd' | 'doNotSubmit', string>;
export const emptyProfile = (): ProfileDraft => ({
  legalName: '', marketingTechnology: '', keywords: '', googleVoice: '', personalPhone: '', dateOfBirth: '', ssnLast4: '', idType: '',
  bachelors: '', masters: '', locationConstraints: '', currentProjectEnd: '', doNotSubmit: '',
});
export const toProfileDraft = (p: ConsultantProfile): ProfileDraft => ({
  legalName: p.legalName ?? '', marketingTechnology: p.marketingTechnology ?? '', keywords: p.keywords ?? '', googleVoice: p.googleVoice ?? '',
  personalPhone: p.personalPhone ?? '', dateOfBirth: p.dateOfBirth?.slice(0, 10) ?? '', ssnLast4: p.ssnLast4 ?? '', idType: p.idType ?? '',
  bachelors: p.bachelors ?? '', masters: p.masters ?? '', locationConstraints: p.locationConstraints ?? '', currentProjectEnd: p.currentProjectEnd ?? '',
  doNotSubmit: p.doNotSubmit ?? '',
});

interface QueuedDoc { kind: string; file: File; expiresOn: string }

/**
 * Add or edit a consultant with every field in one place: the basic record, the marketing intake profile and (optionally) their
 * documents. "Paste intake form" fills the fields from the consultant's onboarding form; nothing is saved until Save.
 */
export function ConsultantEditor({ consultant, startWithIntake, onClose, onSaved, onDelete }: {
  consultant?: Consultant; startWithIntake?: boolean; onClose: () => void; onSaved: (id: number) => void; onDelete?: () => void;
}) {
  const { can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const [id, setId] = useState<number | undefined>(consultant?.id);
  const [draft, setDraft] = useState<ConsultantDraft>(() => (consultant ? { ...toDraft(consultant), id: consultant.id } : emptyDraft()));
  const [prof, setProf] = useState<ProfileDraft>(emptyProfile);
  const [privateOk, setPrivateOk] = useState(true);
  const [loadingProfile, setLoadingProfile] = useState(!!consultant);
  const [docs, setDocs] = useState<QueuedDoc[]>([]);
  const [intake, setIntake] = useState(!!startWithIntake);
  const [intakeText, setIntakeText] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const canEdit = can('bench.edit');

  useEffect(() => {
    if (!consultant) return;
    api<ConsultantProfile>(`/consultants/${consultant.id}/profile`)
      .then(p => { setProf(toProfileDraft(p)); setPrivateOk(p.canSeePrivate); })
      .catch(() => {}).finally(() => setLoadingProfile(false));
  }, [consultant]);

  const fillFromIntake = () => {
    const r = parseIntake(intakeText);
    if (!r.found) { notify('warning', 'Nothing recognised', 'Paste the form with its labels ("Full Legal Name: …").'); return; }
    setProf(p => ({ ...p, ...r.profile }));
    setDraft(d => ({
      ...d, ...r.basics, exp: r.basics.exp ?? d.exp, usExp: r.basics.usExp ?? d.usExp, passportNumber: r.basics.passportNumber ?? d.passportNumber,
      // A new consultant still needs a display name and title: take them from the form when empty.
      name: d.name || r.profile.legalName || '', title: d.title || r.profile.marketingTechnology || '',
    }));
    notify('success', `Filled ${r.found} field${r.found === 1 ? '' : 's'}`, 'Check them below, then Save.');
    setIntake(false); setIntakeText('');
  };

  const save = async () => {
    setBusy(true); setErrors({});
    let savedId = id;
    try {
      // 1. The consultant record (creates it the first time).
      if (savedId) await api(`/consultants/${savedId}`, { method: 'PUT', body: toInput(draft) });
      else { const c = await api<Consultant>('/consultants', { method: 'POST', body: toInput(draft) }); savedId = c.id; setId(c.id); setDraft(d => ({ ...d, id: c.id, rowVersion: c.rowVersion })); }
      // 2. The marketing profile.
      if (canEdit) await api(`/consultants/${savedId}/profile`, { method: 'PUT', body: { ...prof, dateOfBirth: prof.dateOfBirth || null, ssnLast4: privateOk ? prof.ssnLast4 : undefined } });
      // 3. Documents picked here.
      const failed: string[] = [];
      for (const d of docs) {
        try {
          const fd = new FormData(); fd.set('kind', d.kind); fd.set('file', d.file, d.file.name); if (d.expiresOn) fd.set('expiresOn', d.expiresOn);
          await postForm(`/consultants/${savedId}/documents`, fd);
        } catch (e) { failed.push(`${docKindLabel(d.kind)} (${e instanceof ApiError ? e.detail : 'upload failed'})`); }
      }
      if (failed.length) { notify('warning', 'Saved, but some documents did not upload', failed.join('; ')); setDocs(ds => ds.filter(d => failed.some(f => f.startsWith(docKindLabel(d.kind))))); }
      else notify('success', consultant ? 'Consultant saved' : 'Consultant added', `${draft.name}${docs.length ? ` · ${docs.length} document${docs.length === 1 ? '' : 's'} uploaded` : ''}`);
      if (!failed.length) onSaved(savedId!);
    } catch (e) {
      onError(e, setErrors);
      // Created but the profile failed: stay open in edit mode so Save retries the rest.
      if (e instanceof ApiError && e.code === 'CONFLICT' && savedId) notify('info', 'Changed by someone else', 'Close and reopen to get the latest version.');
    } finally { setBusy(false); }
  };

  const p = (k: keyof ProfileDraft) => ({ value: prof[k], onChange: (e: { target: { value: string } }) => setProf({ ...prof, [k]: e.target.value }) });

  return (
    <Modal wide title={consultant ? `Edit ${consultant.name}` : id ? `Finish adding ${draft.name}` : 'Add consultant'} onClose={onClose}
      footer={<>{onDelete && can('bench.delete') && <button className="btn btn-outline" style={{ marginRight: 'auto', color: 'var(--bad)' }} onClick={onDelete}>Delete</button>}
        <button className="btn btn-outline" onClick={onClose}>Cancel</button>
        <button className="btn btn-primary" onClick={save} disabled={busy || loadingProfile}>{busy ? <Spinner /> : consultant || id ? 'Save' : 'Add consultant'}</button></>}>

      <div className="ed-intake">
        {!intake ? <button className="btn btn-sm btn-outline" onClick={() => setIntake(true)}>✦ Paste intake form</button> : (
          <>
            <Field label="Paste the consultant's onboarding form"><textarea className="inp mono" style={{ minHeight: 160, fontSize: 12.5 }} value={intakeText} autoFocus
              onChange={e => setIntakeText(e.target.value)} placeholder={'Full Legal Name:\nJane Doe\n\nTechnology for marketing:\nJava Full Stack with React\n…'} /></Field>
            <div className="flex" style={{ gap: 8 }}><button className="btn btn-sm btn-primary" onClick={fillFromIntake} disabled={!intakeText.trim()}>Fill the fields</button>
              <button className="btn btn-sm btn-outline" onClick={() => setIntake(false)}>Cancel</button>
              <small className="muted">Fills everything it recognises; "NA" is skipped. Nothing is saved until you click Save.</small></div>
          </>)}
      </div>

      <div className="form-sec">Basics</div>
      <ConsultantForm draft={draft} set={setDraft} errors={errors} />

      {canEdit && <>
        <div className="form-sec">Marketing profile</div>
        {loadingProfile ? <Spinner dark /> : <>
          <div className="row2"><Field label="Full legal name" error={errors.legalName}><input className="inp" {...p('legalName')} placeholder="As on the passport" /></Field>
            <Field label="Technology for marketing" error={errors.marketingTechnology}><input className="inp" {...p('marketingTechnology')} placeholder="Java Full Stack with React" /></Field></div>
          <Field label="Job titles to apply for (keywords)" error={errors.keywords}><input className="inp" {...p('keywords')} placeholder="Software Engineer, Java Full Stack Developer…" /></Field>
          <div className="row2"><Field label="Google Voice number" error={errors.googleVoice}><input className="inp" {...p('googleVoice')} /></Field>
            <Field label="Personal cell" error={errors.personalPhone}><input className="inp" {...p('personalPhone')} /></Field></div>
          {privateOk && <div className="row2"><Field label="Date of birth" error={errors.dateOfBirth}><input className="inp" type="date" {...p('dateOfBirth')} /></Field>
            <Field label="SSN (last 4 digits only)" error={errors.ssnLast4}><input className="inp" inputMode="numeric" maxLength={4} {...p('ssnLast4')} /></Field></div>}
          <div className="row2"><Field label="Driver's license / State ID" error={errors.idType}><select className="inp" {...p('idType')}><option value="">—</option>{ID_TYPES.map(t => <option key={t}>{t}</option>)}</select></Field>
            <Field label="Current project end date" error={errors.currentProjectEnd}><input className="inp" {...p('currentProjectEnd')} placeholder="NA, or a date" /></Field></div>
          <div className="row2"><Field label="Bachelor's (stream, university, year)" error={errors.bachelors}><input className="inp" {...p('bachelors')} /></Field>
            <Field label="Master's (stream, university, year)" error={errors.masters}><input className="inp" {...p('masters')} /></Field></div>
          <Field label="Location constraints" error={errors.locationConstraints}><input className="inp" {...p('locationConstraints')} placeholder="Flexible" /></Field>
          <Field label="Vendors / clients not to be submitted to" error={errors.doNotSubmit}><textarea className="inp" style={{ minHeight: 56 }} {...p('doNotSubmit')} /></Field>
        </>}

        <div className="form-sec">Documents {consultant ? '(add more)' : '(optional)'}</div>
        {docs.map((d, i) => (
          <div key={i} className="ed-doc">
            <select className="inp" value={d.kind} onChange={e => setDocs(ds => ds.map((x, j) => (j === i ? { ...x, kind: e.target.value } : x)))} aria-label="Document type">
              {DOC_KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
            <span className="ed-doc-n" title={d.file.name}>📎 {d.file.name}</span>
            <input className="inp" type="date" value={d.expiresOn} title="Expires on (optional)" aria-label="Expires on"
              onChange={e => setDocs(ds => ds.map((x, j) => (j === i ? { ...x, expiresOn: e.target.value } : x)))} />
            <button className="btn btn-sm btn-outline" onClick={() => setDocs(ds => ds.filter((_, j) => j !== i))} aria-label="Remove">✕</button>
          </div>))}
        <label className="btn btn-sm btn-outline" style={{ cursor: 'pointer' }}>+ Add DL, EAD, passport…
          <input type="file" hidden multiple accept=".pdf,.doc,.docx,.jpg,.jpeg,.png,.heic,.webp,.tif,.tiff" onChange={e => {
            const files = [...(e.target.files ?? [])];
            setDocs(ds => [...ds, ...files.map(f => ({ file: f, expiresOn: '', kind: guessKind(f.name) }))]);
            e.target.value = '';
          }} /></label>
        {docs.some(d => d.file.size > 10 * 1024 * 1024) && <Alert type="warning" title="File too large">Documents must be 10 MB or smaller.</Alert>}
        <p className="muted" style={{ fontSize: 12.5 }}>Uploaded when you save. The resume goes through the Resume card on the profile page (or the AI Resume Parser).</p>
      </>}
    </Modal>
  );
}

/** "EAD_front.jpg" → EAD, "passport.pdf" → Passport … so the type is usually right already. */
function guessKind(name: string) {
  const n = name.toLowerCase();
  if (/passport/.test(n)) return 'Passport';
  if (/\bdl\b|driv|licen/.test(n)) return 'DriversLicense';
  if (/state.?id/.test(n)) return 'StateId';
  if (/ead/.test(n)) return 'EAD';
  if (/i-?20/.test(n)) return 'I20';
  if (/i-?797/.test(n)) return 'I797';
  if (/visa/.test(n)) return 'Visa';
  if (/ssn|social/.test(n)) return 'SSN';
  if (/degree|transcript|diploma/.test(n)) return 'Degree';
  return 'Other';
}
