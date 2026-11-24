import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { api, ApiError, Consultant, ConsultantDocument, ConsultantProfile, download, postForm, ResumeSource } from '../api';
import { AssignDialog } from '../assign';
import { useAuth } from '../auth';
import { roleLabel } from '../roles';
import { ConsultantEditor, docKindLabel as kindLabel, DOC_KINDS as KINDS } from '../consultantEditor';
import { fileToText, storeResumeFile } from '../resumeFile';
import { Avatar, Empty, Field, FieldErrors, Modal, Spinner, statusClass, useConfirm, useErrorHandler, useToast, visaClass } from '../ui';

const size = (b: number) => (b >= 1024 * 1024 ? `${(b / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);
const day = (iso?: string | null) => (iso ? new Date(iso.slice(0, 10) + 'T12:00:00').toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' }) : '');

/** One consultant: who they are, the marketing intake details, their resume and their documents. */
export default function ConsultantProfilePage() {
  const { id } = useParams();
  const cid = Number(id);
  const { can } = useAuth();
  const nav = useNavigate();
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [c, setC] = useState<Consultant | null>(null);
  const [p, setP] = useState<ConsultantProfile | null>(null);
  const [docs, setDocs] = useState<ConsultantDocument[] | null>(null);
  const [resume, setResume] = useState<ResumeSource | null | undefined>(undefined);
  const [edit, setEdit] = useState<false | 'edit' | 'intake'>(false);
  const [assign, setAssign] = useState(false);
  const [upload, setUpload] = useState(false);
  const [busy, setBusy] = useState(false);
  const resumeInput = useRef<HTMLInputElement>(null);

  const load = useCallback(() => {
    api<Consultant>(`/consultants/${cid}`).then(setC).catch(e => { onError(e); nav('/bench', { replace: true }); });
    api<ConsultantProfile>(`/consultants/${cid}/profile`).then(setP).catch(() => setP(null));
    api<ConsultantDocument[]>(`/consultants/${cid}/documents`).then(setDocs).catch(() => setDocs([]));
    api<ResumeSource | undefined>(`/resumes/source/${cid}`).then(s => setResume(s ?? null)).catch(() => setResume(null));
  }, [cid, onError, nav]);
  useEffect(load, [load]);

  if (!c) return <Empty><Spinner dark /> Loading…</Empty>;
  const canEdit = can('bench.edit');

  const remove = async (force = false): Promise<void> => {
    if (!force && !await confirm({ title: `Delete ${c.name}?`, message: `${c.name} will be removed from the bench.`, okLabel: 'Delete', danger: true })) return;
    try { await api(`/consultants/${cid}`, { method: 'DELETE', query: { force } }); notify('success', 'Consultant removed', c.name); nav('/bench'); }
    catch (e) {
      if (e instanceof ApiError && e.code === 'HAS_ACTIVE_SUBMISSIONS' && await confirm({ title: e.title, message: e.detail, okLabel: 'Withdraw & delete', danger: true })) return remove(true);
      onError(e);
    }
  };
  const uploadResume = async (f: File) => {
    setBusy(true);
    try {
      const text = (await fileToText(f)).trim();
      const s = await api<ResumeSource>('/resumes/source', { method: 'POST', body: { consultantId: cid, text, fileName: f.name } });
      const problem = await storeResumeFile(cid, f, s.resumeId);
      notify(problem ? 'warning' : 'success', 'Resume saved', problem ? `The text was saved, but the file couldn't be stored: ${problem}` : `${f.name} is now ${c.name}'s latest resume.`);
      load();
    } catch (e) {
      if (e instanceof Error && !('status' in e)) notify('warning', "Couldn't read that file", e.message); else onError(e);
    } finally { setBusy(false); if (resumeInput.current) resumeInput.current.value = ''; }
  };
  const removeDoc = async (d: ConsultantDocument) => {
    if (!await confirm({ title: 'Delete document?', message: `${kindLabel(d.kind)}: ${d.fileName}`, okLabel: 'Delete', danger: true })) return;
    try { await api(`/consultants/${cid}/documents/${d.id}`, { method: 'DELETE' }); notify('success', 'Document deleted'); load(); } catch (e) { onError(e); }
  };

  const row = (label: string, value?: string | number | null, mono?: boolean) => (
    <div className="pf-row"><span>{label}</span><b className={mono ? 'mono' : ''}>{value === undefined || value === null || value === '' ? <i className="muted">—</i> : value}</b></div>);

  return (
    <>
      <div className="ph">
        <div className="pf-head">
          <Link to="/bench" className="muted" style={{ fontSize: 13 }}>← Bench</Link>
          <div className="who" style={{ marginTop: 6 }}><Avatar name={c.name} size={52} /><div>
            <h1 style={{ margin: 0 }}>{c.name}</h1>
            <p style={{ margin: 0 }}>{c.title} · {c.exp}+ yrs · {c.location}</p>
            <div style={{ marginTop: 4 }}><span className={`tag ${statusClass(c.status)}`}>{c.status}</span><span className={`tag ${visaClass(c.visa)}`}>{c.visa || 'Visa ?'}</span>
              <span className="tag s">Assigned to {c.ownerName}{c.ownerRole ? ` (${roleLabel(c.ownerRole)})` : ''}</span>
              {c.ownerRole === 'Team Lead' && <span className="tag y">Needs an executive</span>}</div>
          </div></div>
        </div>
        <div className="ph-act">
          {(can('bench.assign') || can('bench.reassign')) && <button className="btn btn-outline" onClick={() => setAssign(true)}>Assign</button>}
          {!can('bench.assign') && can('board.view') && <button className="btn btn-primary" onClick={() => nav(`/external-jobs?consultant=${cid}`)}>Match jobs</button>}
          {can('parser.use') && <button className="btn btn-outline" onClick={() => nav(`/resume-builder?consultant=${cid}`)}>✦ Resume Builder</button>}
          {canEdit && <button className="btn btn-outline" onClick={() => setEdit('edit')}>Edit</button>}
          {can('bench.delete') && <button className="btn btn-outline" style={{ color: 'var(--bad)' }} onClick={() => remove()}>Delete</button>}
        </div>
      </div>

      <div className="pf-grid">
        <div className="card">
          <div className="card-h"><h3>Marketing profile</h3>
            {canEdit && p && <div className="flex" style={{ gap: 8 }}>
              <button className="btn btn-sm btn-outline" onClick={() => setEdit('intake')}>✦ Paste intake form</button>
              <button className="btn btn-sm btn-outline" onClick={() => setEdit('edit')}>Edit</button></div>}</div>
          <div className="card-b pf-cols">
            <div>
              <h4>Identity</h4>
              {row('Full legal name', p?.legalName)}
              {row('Visa status', c.visa)}{row('Visa valid until', day(c.visaValidUntil))}
              {p?.canSeePrivate && row('Date of birth', day(p.dateOfBirth))}
              {p?.canSeePrivate && row('SSN (last 4)', p.ssnLast4 ? `•••-••-${p.ssnLast4}` : '', true)}
              {row('Passport', c.passportOnFile, true)}{row("Driver's license / State ID", p?.idType)}
              <h4>Contact</h4>
              {row('Marketing email', c.email)}{row('Google Voice', p?.googleVoice, true)}{row('Main phone', c.phone, true)}{row('Personal cell', p?.personalPhone, true)}
              {row('LinkedIn', c.linkedInUrl ? <a href={c.linkedInUrl} target="_blank" rel="noreferrer">{c.linkedInUrl.replace(/^https?:\/\/(www\.)?/, '')}</a> as unknown as string : '')}
            </div>
            <div>
              <h4>Marketing</h4>
              {row('Technology', p?.marketingTechnology)}{row('Job titles (keywords)', p?.keywords)}
              {row('Total experience', `${c.exp} yrs`)}{row('US experience', c.usExp != null ? `${c.usExp} yrs` : '')}
              {row('Pay rate', `$${c.rate}/hr`)}{row('Availability', c.availability)}{row('Current project ends', p?.currentProjectEnd)}
              {row('Current location', c.location)}{row('Location constraints', p?.locationConstraints)}{row('Preferred locations', c.preferredLocations)}
              <h4>Education</h4>
              {row("Bachelor's", p?.bachelors)}{row("Master's", p?.masters)}{row('Certifications', c.certifications)}
              <h4>Restrictions</h4>
              {row('Do not submit to', p?.doNotSubmit)}
            </div>
          </div>
          <div className="card-b" style={{ borderTop: '1px solid var(--line)' }}><b style={{ fontSize: 13 }}>Skills</b><div style={{ marginTop: 6 }}>{c.skills.map(s => <span key={s} className="tag">{s}</span>)}</div>
            {c.summary && <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>{c.summary}</p>}</div>
        </div>

        <div>
          <div className="card">
            <div className="card-h"><h3>Resume</h3>
              {canEdit && <><button className="btn btn-sm btn-outline" onClick={() => resumeInput.current?.click()} disabled={busy}>{busy ? <Spinner dark /> : 'Upload new'}</button>
                <input ref={resumeInput} type="file" hidden accept=".pdf,.docx,.doc,.rtf,.txt,.md" onChange={e => e.target.files?.[0] && uploadResume(e.target.files[0])} /></>}</div>
            <div className="card-b">
              {resume === undefined ? <Spinner dark /> : resume === null ? <p className="muted" style={{ margin: 0 }}>No resume on file yet.</p> : (
                <div className="doc-row">
                  <span className="doc-ic">📄</span>
                  <div style={{ flex: 1, minWidth: 0 }}><b>{resume.fileName ?? 'Resume'}</b><small className="muted">Latest resume · the one attached to submissions and the hotlist</small></div>
                  <button className="btn btn-sm btn-outline" onClick={() => download(`/resumes/submission/${cid}`, {}, `${c.name} - Resume.docx`).catch(onError)}>Download</button>
                </div>)}
            </div>
          </div>

          <div className="card" style={{ marginTop: 16 }}>
            <div className="card-h"><h3>Documents{docs ? ` (${docs.length})` : ''}</h3>{canEdit && <button className="btn btn-sm btn-primary" onClick={() => setUpload(true)}>+ Upload</button>}</div>
            <div className="card-b">
              {docs === null ? <Spinner dark /> : docs.length === 0 ? <p className="muted" style={{ margin: 0 }}>No documents yet. Upload copies of the DL, EAD, passport, I-20 and so on.</p>
                : docs.map(d => {
                  const expired = d.expiresOn && new Date(d.expiresOn) < new Date();
                  return (
                    <div key={d.id} className="doc-row">
                      <span className="doc-ic">{d.contentType.startsWith('image/') ? '🖼️' : '📄'}</span>
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <b>{kindLabel(d.kind)}</b>{d.expiresOn && <span className={`tag ${expired ? 'r' : 's'}`}>{expired ? 'Expired' : 'Expires'} {day(d.expiresOn)}</span>}
                        <small className="muted">{d.fileName} · {size(d.sizeBytes)} · {d.uploadedBy}, {day(d.uploadedUtc)}{d.note ? ` · ${d.note}` : ''}</small>
                      </div>
                      <button className="btn btn-sm btn-outline" onClick={() => download(`/consultants/${cid}/documents/${d.id}`, {}, d.fileName).catch(onError)}>Download</button>
                      {can('bench.delete') && <button className="btn btn-sm btn-outline" style={{ color: 'var(--bad)' }} onClick={() => removeDoc(d)} aria-label="Delete document">✕</button>}
                    </div>);
                })}
            </div>
          </div>
        </div>
      </div>

      {edit && <ConsultantEditor consultant={c} startWithIntake={edit === 'intake'} onClose={() => setEdit(false)} onSaved={() => { setEdit(false); load(); }} />}
      {upload && <UploadDialog cid={cid} name={c.name} onClose={() => setUpload(false)} onDone={() => { setUpload(false); load(); }} />}
      {assign && <AssignDialog ids={[cid]} names={[c.name]} currentOwnerId={c.ownerId} onClose={() => setAssign(false)} onDone={() => { setAssign(false); load(); }} />}
    </>
  );
}


function UploadDialog({ cid, name, onClose, onDone }: { cid: number; name: string; onClose: () => void; onDone: () => void }) {
  const onError = useErrorHandler();
  const notify = useToast();
  const [kind, setKind] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [expires, setExpires] = useState('');
  const [note, setNote] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setErrors({});
    try {
      const fd = new FormData();
      fd.set('kind', kind); if (file) fd.set('file', file, file.name); if (expires) fd.set('expiresOn', expires); if (note.trim()) fd.set('note', note.trim());
      await postForm(`/consultants/${cid}/documents`, fd);
      notify('success', `${kindLabel(kind)} uploaded`, `Saved to ${name}'s documents.`);
      onDone();
    } catch (e) { onError(e, setErrors); } finally { setBusy(false); }
  };
  return (
    <Modal title={`Upload a document for ${name}`} onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save} disabled={busy || !kind || !file}>{busy ? <Spinner /> : 'Upload'}</button></>}>
      <Field label="Document *" error={errors.kind}><select className="inp" value={kind} onChange={e => setKind(e.target.value)}><option value="">Choose…</option>{KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
      <Field label="File * (PDF, Word or a photo, up to 10 MB)" error={errors.file}><input className="inp" type="file" accept=".pdf,.doc,.docx,.jpg,.jpeg,.png,.heic,.webp,.tif,.tiff" onChange={e => setFile(e.target.files?.[0] ?? null)} /></Field>
      <div className="row2"><Field label="Expires on" error={errors.expiresOn}><input className="inp" type="date" value={expires} onChange={e => setExpires(e.target.value)} /></Field>
        <Field label="Note" error={errors.note}><input className="inp" value={note} onChange={e => setNote(e.target.value)} maxLength={300} placeholder="e.g. front and back" /></Field></div>
      <p className="muted" style={{ fontSize: 12.5 }}>To add a resume, use <b>Upload new</b> on the Resume card: it becomes the resume attached to submissions.</p>
    </Modal>
  );
}
