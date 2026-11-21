import { useCallback, useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, Consultant, download, fetchFile, Requirement, ResumeDoc, ResumeSource, TailoredResume } from '../api';
import { useAuth } from '../auth';
import { ComposeDialog } from '../compose';
import { fileToText } from '../resumeFile';
import { AiBadge, Alert, Empty, Field, FieldErrors, Modal, Spinner, ago, useConfirm, useErrorHandler, useToast } from '../ui';

type Job = ResumeDoc['experience'][number];
const esc = (s: string) => s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]!));

export default function ResumeBuilder() {
  const { can, scope } = useAuth();
  const notify = useToast();
  const onError = useErrorHandler();
  const confirm = useConfirm();
  const [params, setParams] = useSearchParams();
  const [cons, setCons] = useState<Consultant[]>([]);
  const [reqs, setReqs] = useState<Requirement[]>([]);
  const cid = Number(params.get('consultant')) || cons[0]?.id || 0;
  const consultant = cons.find(c => c.id === cid);
  const [source, setSource] = useState<ResumeSource | null | undefined>(undefined);
  const [uploading, setUploading] = useState(false);
  const [showSource, setShowSource] = useState(false);
  const [versions, setVersions] = useState<TailoredResume[]>([]);
  const [cur, setCur] = useState<TailoredResume | null>(null);
  const [doc, setDoc] = useState<ResumeDoc | null>(null);
  const [name, setName] = useState('');
  const [dirty, setDirty] = useState(false);
  const [view, setView] = useState<'preview' | 'edit'>('preview');
  // ?requirement= preselects the job (e.g. when opened from the Match Agent).
  const [t, setT] = useState({ mode: 'req' as 'req' | 'jd', requirementId: params.get('requirement') ?? '', jobTitle: '', client: '', jd: '', hideContact: true, focus: '' });
  const [errors, setErrors] = useState<FieldErrors>({});
  const [tailoring, setTailoring] = useState(false);
  const [saving, setSaving] = useState(false);
  const [email, setEmail] = useState<File | null>(null);

  useEffect(() => {
    api<Consultant[]>('/consultants', { query: { scope } }).then(c => setCons(c.filter(x => x.status !== 'Inactive'))).catch(onError);
    api<Requirement[]>('/requirements', { query: { status: 'Open' } }).then(setReqs).catch(onError);
  }, [scope, onError]);

  const open = useCallback((v: TailoredResume | null) => {
    setCur(v); setDoc(v ? structuredClone(v.doc) : null); setName(v?.name ?? ''); setDirty(false); setView('preview');
  }, []);

  const loadVersions = useCallback(async (keep?: number) => {
    if (!cid) return;
    try {
      const list = await api<TailoredResume[]>('/resumes', { query: { consultantId: cid } });
      setVersions(list);
      open(list.find(v => v.id === keep) ?? list[0] ?? null);
    } catch (e) { onError(e); }
  }, [cid, onError, open]);

  useEffect(() => {
    if (!cid) return;
    setSource(undefined); open(null);
    api<ResumeSource | undefined>(`/resumes/source/${cid}`).then(s => setSource(s ?? null)).catch(e => { setSource(null); onError(e); });
    loadVersions();
  }, [cid]); // eslint-disable-line react-hooks/exhaustive-deps

  const upload = async (f: File) => {
    setUploading(true);
    try {
      const text = (await fileToText(f)).trim();
      const s = await api<ResumeSource>('/resumes/source', { method: 'POST', body: { consultantId: cid, text, fileName: f.name } });
      setSource(s);
      notify('success', 'Original resume saved', `${f.name} is now the source for tailored versions.`);
    } catch (e) {
      if (e instanceof Error && !('status' in e)) notify('warning', "Couldn't read that file", e.message);
      else onError(e);
    } finally { setUploading(false); }
  };

  const tailor = async () => {
    setTailoring(true); setErrors({});
    try {
      const body = t.mode === 'req'
        ? { consultantId: cid, requirementId: Number(t.requirementId) || undefined, hideContact: t.hideContact, focus: t.focus }
        : { consultantId: cid, jobTitle: t.jobTitle, client: t.client, jobDescription: t.jd, hideContact: t.hideContact, focus: t.focus };
      if (t.mode === 'req' && !t.requirementId) { setErrors({ requirementId: ['Pick the requirement'] }); return; }
      const v = await api<TailoredResume>('/resumes/tailor', { method: 'POST', body });
      notify(v.warnings.length ? 'warning' : 'success', 'Tailored resume ready',
        v.warnings.length ? `Review ${v.warnings.length} item${v.warnings.length === 1 ? '' : 's'} flagged as not in the original before sending.` : 'Review the changes and gaps, then download or email it.');
      setVersions(vs => [v, ...vs]);
      open(v);
    } catch (e) { onError(e, setErrors); }
    finally { setTailoring(false); }
  };

  // Opened with ?auto=1 ("Tailor resume" on the Match Agent): start once we know whether a resume is on file. Runs once;
  // "auto" is then removed from the URL so a page refresh doesn't create another version.
  const autoRan = useRef(false);
  useEffect(() => {
    if (autoRan.current || params.get('auto') !== '1' || source === undefined) return;
    autoRan.current = true;
    const next = new URLSearchParams(params);
    next.delete('auto');
    setParams(next, { replace: true });
    if (source) tailor();
    else notify('info', 'Upload the resume first', `${consultant?.name ?? 'This consultant'} has no resume on file yet. Upload it, then click Tailor resume.`);
  }, [source]); // eslint-disable-line react-hooks/exhaustive-deps

  const save = async () => {
    if (!cur || !doc) return;
    setSaving(true);
    try {
      const v = await api<TailoredResume>(`/resumes/${cur.id}`, { method: 'PUT', body: { name, doc } });
      setVersions(vs => vs.map(x => (x.id === v.id ? v : x)));
      open(v);
      notify(v.warnings.length ? 'warning' : 'success', 'Changes saved', v.warnings.length ? `${v.warnings.length} item(s) aren't in the original resume. Check them before sending.` : undefined);
    } catch (e) { onError(e); }
    finally { setSaving(false); }
  };

  const remove = async (v: TailoredResume) => {
    if (!await confirm({ title: `Delete "${v.name}"?`, message: 'This tailored version will be deleted. The original resume stays.', okLabel: 'Delete', danger: true })) return;
    try { await api(`/resumes/${v.id}`, { method: 'DELETE' }); notify('success', 'Version deleted'); loadVersions(); } catch (e) { onError(e); }
  };

  const guard = async () => !dirty || confirm({ title: 'Unsaved changes', message: 'Save your edits first, or the download will use the last saved version.', okLabel: 'Continue anyway' });
  const downloadDocx = async () => { if (cur && await guard()) download(`/resumes/${cur.id}/docx`, {}, 'resume.docx').catch(onError); };
  const emailVendor = async () => {
    if (!cur || !await guard()) return;
    try {
      const f = await fetchFile(`/resumes/${cur.id}/docx`);
      setEmail(new File([f.blob], f.name ?? 'resume.docx', { type: f.blob.type }));
    } catch (e) { onError(e); }
  };
  const print = () => {
    if (!doc) return;
    const w = window.open('', '_blank');
    if (!w) { notify('warning', 'Pop-up blocked', 'Allow pop-ups for this site to print or save as PDF.'); return; }
    w.document.write(`<!doctype html><html><head><meta charset="utf-8"><title>${esc(doc.name)} - Resume</title><style>${PRINT_CSS}</style></head><body>${previewHtml(doc)}</body></html>`);
    w.document.close();
    w.focus();
    setTimeout(() => w.print(), 300);
  };

  const edit = (d: ResumeDoc) => { setDoc(d); setDirty(true); };
  const req = reqs.find(r => String(r.id) === t.requirementId);

  return (
    <>
      <div className="ph"><div><h1>AI Resume Builder</h1>
        <p>Tailor a consultant's resume to a client's requirement. The AI rewords, reorders and emphasises what the resume already says. It never adds skills, employers, dates or numbers.</p></div></div>

      <div className="rb-grid">
        <aside className="card"><div className="card-b">
          <h4 className="rb-h">1. Consultant</h4>
          <select className="inp" value={cid || ''} onChange={e => setParams({ consultant: e.target.value })} aria-label="Consultant">
            {cons.length === 0 && <option value="">No consultants in your view</option>}
            {cons.map(c => <option key={c.id} value={c.id}>{c.name} · {c.title}</option>)}
          </select>
          <div className="rb-source">
            {source === undefined ? <span className="muted"><Spinner dark /> Checking resume…</span>
              : source ? <><b>Original resume</b><small>{source.fileName ?? 'Pasted text'} · {ago(source.uploadedUtc)}{source.uploadedBy ? ` by ${source.uploadedBy}` : ''}</small>
                <button className="btn btn-sm" style={{ color: 'var(--primary)', padding: 0 }} onClick={() => setShowSource(true)}>View text</button></>
              : <Alert type="info" title="No resume on file">Upload the consultant's current resume. It's the only source of facts for tailoring.</Alert>}
            {can('bench.edit') && cid > 0 && <label className="btn btn-sm btn-outline" style={{ marginTop: 8 }}>
              {uploading ? <Spinner dark /> : source ? 'Replace resume' : 'Upload resume (PDF, DOCX, TXT)'}
              <input type="file" hidden accept=".pdf,.docx,.txt,.md" onChange={e => { const f = e.target.files?.[0]; e.target.value = ''; if (f) upload(f); }} />
            </label>}
          </div>

          <h4 className="rb-h">2. Client requirement</h4>
          <div className="tabs" style={{ marginBottom: 10 }}>
            <button className={t.mode === 'req' ? 'on' : ''} onClick={() => setT({ ...t, mode: 'req' })}>From requirements</button>
            <button className={t.mode === 'jd' ? 'on' : ''} onClick={() => setT({ ...t, mode: 'jd' })}>Paste job description</button>
          </div>
          {t.mode === 'req' ? <>
            <Field error={errors.requirementId}><select className="inp" value={t.requirementId} onChange={e => setT({ ...t, requirementId: e.target.value })} aria-label="Requirement">
              <option value="">Pick a requirement…</option>{reqs.map(r => <option key={r.id} value={r.id}>{r.title} · {r.client}</option>)}</select></Field>
            {req && <div style={{ margin: '-6px 0 10px' }}>{req.skills.map(s => <span key={s} className={`tag ${consultant?.skills.some(x => x.toLowerCase() === s.toLowerCase()) ? 'g' : 's'}`}>{s}</span>)}</div>}
          </> : <>
            <div className="row2">
              <Field label="Job title *" error={errors.jobTitle}><input className="inp" value={t.jobTitle} onChange={e => setT({ ...t, jobTitle: e.target.value })} /></Field>
              <Field label="Client" error={errors.client}><input className="inp" value={t.client} onChange={e => setT({ ...t, client: e.target.value })} /></Field>
            </div>
            <Field label="Job description *" error={errors.jobDescription}><textarea className="inp" style={{ minHeight: 120 }} value={t.jd} onChange={e => setT({ ...t, jd: e.target.value })} placeholder="Paste the client's job description or the vendor's email" /></Field>
          </>}

          <h4 className="rb-h">3. Options</h4>
          <label className="check" style={{ borderBottom: 0, paddingTop: 0 }}><input type="checkbox" checked={t.hideContact} onChange={e => setT({ ...t, hideContact: e.target.checked })} />
            <span>Hide email and phone (vendors get contact details after the RTR)</span></label>
          <Field label="Emphasis (optional)" error={errors.focus}><input className="inp" value={t.focus} onChange={e => setT({ ...t, focus: e.target.value })} placeholder="e.g. banking domain, Kafka streaming" maxLength={500} /></Field>
          <button className="btn btn-dark" style={{ width: '100%', justifyContent: 'center' }} onClick={tailor} disabled={tailoring || !source || !can('parser.use')}>
            {tailoring ? <><Spinner /> Tailoring… this can take up to a minute</> : '✦ Tailor resume'}</button>

          {versions.length > 0 && <>
            <h4 className="rb-h">Saved versions</h4>
            {versions.map(v => (
              <div key={v.id} className={`rb-ver${cur?.id === v.id ? ' on' : ''}`}>
                <button onClick={() => open(v)}><b>{v.name}</b><small>{ago(v.updatedUtc)} · {v.engine === 'claude' ? 'AI' : 'Built-in'}{v.warnings.length ? ` · ⚠ ${v.warnings.length}` : ''}</small></button>
                {can('bench.edit') && <button className="x" aria-label={`Delete ${v.name}`} onClick={() => remove(v)}>✕</button>}
              </div>))}
          </>}
        </div></aside>

        <section className="card">{!cur || !doc ? <Empty>{source ? 'Pick the requirement and click "Tailor resume".' : 'Upload the original resume to get started.'}</Empty> : <>
          <div className="card-h">
            <div className="flex" style={{ flex: 1, minWidth: 0 }}>
              <input className="inp" style={{ maxWidth: 360, fontWeight: 600 }} value={name} onChange={e => { setName(e.target.value); setDirty(true); }} aria-label="Version name" />
              <AiBadge source={cur.engine} />
            </div>
            <div className="flex">
              <div className="tabs"><button className={view === 'preview' ? 'on' : ''} onClick={() => setView('preview')}>Preview</button><button className={view === 'edit' ? 'on' : ''} onClick={() => setView('edit')}>Edit</button></div>
              {dirty && <button className="btn btn-sm btn-primary" onClick={save} disabled={saving}>{saving ? <Spinner /> : 'Save changes'}</button>}
              <button className="btn btn-sm btn-outline" onClick={downloadDocx}>⬇ Word</button>
              <button className="btn btn-sm btn-outline" onClick={print}>Print / PDF</button>
              <button className="btn btn-sm btn-outline" onClick={emailVendor}>✉ Email to vendor</button>
            </div>
          </div>
          <div className="card-b">
            <Review v={cur} />
            {view === 'preview' ? <div className="resume-preview" dangerouslySetInnerHTML={{ __html: previewHtml(doc) }} /> : <Editor doc={doc} onChange={edit} />}
          </div>
        </>}</section>
      </div>

      {showSource && source && <Modal wide title={`Original resume · ${consultant?.name ?? ''}`} onClose={() => setShowSource(false)}><div className="jd">{source.text}</div></Modal>}
      {email && cur && <ComposeDialog initialFiles={[email]} consultantId={cur.consultantId} onClose={() => setEmail(null)}
        initial={{ subject: `${consultant?.title || 'Candidate'}: ${cur.consultantName} for ${cur.targetTitle}${cur.targetClient ? ` (${cur.targetClient})` : ''}` }} />}
    </>
  );
}

/** What the AI changed, what's missing, and anything that couldn't be verified. Shown above every version. */
function Review({ v }: { v: TailoredResume }) {
  const [showChanges, setShowChanges] = useState(false);
  return (
    <div style={{ marginBottom: 16 }}>
      {v.notice && <Alert type="info" title="Built-in engine">{v.notice}</Alert>}
      {v.warnings.length > 0 && <Alert type="warning" title={`Check ${v.warnings.length} item${v.warnings.length === 1 ? '' : 's'} before sending`}>
        <ul className="rb-list">{v.warnings.map(w => <li key={w}>{w}</li>)}</ul></Alert>}
      <div className="rb-review">
        <div><small>Matches the job</small><div>{v.matched.length ? v.matched.map(s => <span key={s} className="tag g">{s}</span>) : <span className="muted">None found</span>}</div></div>
        <div><small>Gaps: not in the resume, so not added</small><div>{v.gaps.length ? v.gaps.map(s => <span key={s} className="tag r">{s}</span>) : <span className="muted">None</span>}</div>
          {v.gaps.length > 0 && <p className="muted" style={{ fontSize: 12, marginTop: 4 }}>Cover these in screening, or ask the consultant whether they have the experience and update the original resume.</p>}</div>
      </div>
      {v.changes.length > 0 && <button className="btn btn-sm" style={{ color: 'var(--primary)', padding: 0 }} onClick={() => setShowChanges(s => !s)}>
        {showChanges ? 'Hide' : 'Show'} what changed ({v.changes.length})</button>}
      {showChanges && <ul className="rb-list" style={{ marginTop: 6 }}>{v.changes.map(c => <li key={c}>{c}</li>)}</ul>}
    </div>
  );
}

function Editor({ doc, onChange }: { doc: ResumeDoc; onChange: (d: ResumeDoc) => void }) {
  const set = (patch: Partial<ResumeDoc>) => onChange({ ...doc, ...patch });
  const setJob = (i: number, patch: Partial<Job>) => set({ experience: doc.experience.map((j, k) => (k === i ? { ...j, ...patch } : j)) });
  const move = (i: number, d: -1 | 1) => { const e = [...doc.experience]; [e[i], e[i + d]] = [e[i + d], e[i]]; set({ experience: e }); };
  const lines = (s: string) => s.split('\n').map(x => x.trim()).filter(Boolean);
  return (
    <div className="rb-editor">
      <div className="row2">
        <Field label="Headline"><input className="inp" value={doc.headline} onChange={e => set({ headline: e.target.value })} /></Field>
        <Field label="Location"><input className="inp" value={doc.location ?? ''} onChange={e => set({ location: e.target.value })} /></Field>
      </div>
      <Field label="Professional summary"><textarea className="inp" style={{ minHeight: 100 }} value={doc.summary} onChange={e => set({ summary: e.target.value })} /></Field>

      <h4 className="rb-h">Technical skills</h4>
      {doc.skills.map((g, i) => (
        <div key={i} className="rb-skill">
          <input className="inp" value={g.category} placeholder="Category" onChange={e => set({ skills: doc.skills.map((x, k) => (k === i ? { ...x, category: e.target.value } : x)) })} />
          <input className="inp" value={g.items.join(', ')} placeholder="Skills, comma separated"
            onChange={e => set({ skills: doc.skills.map((x, k) => (k === i ? { ...x, items: e.target.value.split(',').map(s => s.trim()) } : x)) })} />
          <button className="btn btn-sm" aria-label="Remove group" onClick={() => set({ skills: doc.skills.filter((_, k) => k !== i) })}>✕</button>
        </div>))}
      <button className="btn btn-sm btn-outline" onClick={() => set({ skills: [...doc.skills, { category: '', items: [] }] })}>+ Skill group</button>

      <h4 className="rb-h">Professional experience</h4>
      {doc.experience.map((j, i) => (
        <div key={i} className="rb-job">
          <div className="flex between"><b>{j.title || 'Role'}{j.company ? ` · ${j.company}` : ''}</b>
            <span className="flex">
              <button className="btn btn-sm" disabled={i === 0} onClick={() => move(i, -1)} aria-label="Move up">↑</button>
              <button className="btn btn-sm" disabled={i === doc.experience.length - 1} onClick={() => move(i, 1)} aria-label="Move down">↓</button>
              <button className="btn btn-sm" onClick={() => set({ experience: doc.experience.filter((_, k) => k !== i) })} aria-label="Remove role">✕</button>
            </span></div>
          <div className="row2">
            <Field label="Title"><input className="inp" value={j.title} onChange={e => setJob(i, { title: e.target.value })} /></Field>
            <Field label="Company"><input className="inp" value={j.company} onChange={e => setJob(i, { company: e.target.value })} /></Field>
          </div>
          <div className="row3">
            <Field label="Location"><input className="inp" value={j.location ?? ''} onChange={e => setJob(i, { location: e.target.value })} /></Field>
            <Field label="Start"><input className="inp" value={j.start ?? ''} onChange={e => setJob(i, { start: e.target.value })} /></Field>
            <Field label="End"><input className="inp" value={j.end ?? ''} onChange={e => setJob(i, { end: e.target.value })} /></Field>
          </div>
          <Field label="Bullets (one per line)"><textarea className="inp" style={{ minHeight: 120 }} value={j.bullets.join('\n')} onChange={e => setJob(i, { bullets: e.target.value.split('\n') })} onBlur={e => setJob(i, { bullets: lines(e.target.value) })} /></Field>
          <Field label="Environment"><input className="inp" value={j.environment ?? ''} onChange={e => setJob(i, { environment: e.target.value })} /></Field>
        </div>))}
      {doc.additionalText != null && <Field label="Experience (original text, kept as-is)"><textarea className="inp" style={{ minHeight: 240 }} value={doc.additionalText} onChange={e => set({ additionalText: e.target.value })} /></Field>}

      <h4 className="rb-h">Education</h4>
      {doc.education.map((ed, i) => (
        <div key={i} className="rb-skill">
          <input className="inp" value={ed.degree} placeholder="Degree" onChange={e => set({ education: doc.education.map((x, k) => (k === i ? { ...x, degree: e.target.value } : x)) })} />
          <input className="inp" value={ed.school ?? ''} placeholder="School" onChange={e => set({ education: doc.education.map((x, k) => (k === i ? { ...x, school: e.target.value } : x)) })} />
          <input className="inp" style={{ maxWidth: 90 }} value={ed.year ?? ''} placeholder="Year" onChange={e => set({ education: doc.education.map((x, k) => (k === i ? { ...x, year: e.target.value } : x)) })} />
          <button className="btn btn-sm" aria-label="Remove education" onClick={() => set({ education: doc.education.filter((_, k) => k !== i) })}>✕</button>
        </div>))}
      <button className="btn btn-sm btn-outline" onClick={() => set({ education: [...doc.education, { degree: '' }] })}>+ Education</button>
      <Field label="Certifications (one per line)" className="mt"><textarea className="inp" style={{ minHeight: 60 }} value={doc.certifications.join('\n')} onChange={e => set({ certifications: e.target.value.split('\n') })} onBlur={e => set({ certifications: lines(e.target.value) })} /></Field>
      <p className="muted" style={{ fontSize: 12.5 }}>When you save, the resume is checked against the original again and anything not in it is flagged.</p>
    </div>
  );
}

/** Same layout as the Word export, for the preview and for printing to PDF. */
function previewHtml(d: ResumeDoc): string {
  const sec = (title: string, inner: string) => (inner ? `<h2>${esc(title)}</h2>${inner}` : '');
  const contact = [d.location, d.email, d.phone].filter(Boolean).map(x => esc(x!)).join(' &nbsp;|&nbsp; ');
  const jobs = d.experience.map(j => `<div class="job"><div class="jh"><b>${esc(j.title)}${j.company ? ` &nbsp;|&nbsp; ${esc(j.company)}` : ''}</b><span>${esc([j.start, j.end].filter(Boolean).join(' – '))}</span></div>
    ${j.location ? `<div class="loc">${esc(j.location)}</div>` : ''}<ul>${j.bullets.filter(Boolean).map(b => `<li>${esc(b)}</li>`).join('')}</ul>
    ${j.environment ? `<div class="env"><b>Environment:</b> ${esc(j.environment)}</div>` : ''}</div>`).join('');
  const extra = d.additionalText ? `<div class="raw">${esc(d.additionalText)}</div>` : '';
  return `<div class="rv"><h1>${esc(d.name)}</h1>${d.headline ? `<div class="hl">${esc(d.headline)}</div>` : ''}${contact ? `<div class="ct">${contact}</div>` : ''}
    ${sec('Professional Summary', d.summary ? `<p>${esc(d.summary)}</p>` : '')}
    ${sec('Technical Skills', d.skills.map(g => `<p><b>${esc(g.category)}:</b> ${esc(g.items.filter(Boolean).join(', '))}</p>`).join(''))}
    ${sec('Professional Experience', jobs + extra)}
    ${sec('Education', d.education.map(e => `<p><b>${esc(e.degree)}</b>${[e.school, e.year].filter(Boolean).length ? ` &nbsp;|&nbsp; ${esc([e.school, e.year].filter(Boolean).join(', '))}` : ''}</p>`).join(''))}
    ${sec('Certifications', d.certifications.length ? `<ul>${d.certifications.map(c => `<li>${esc(c)}</li>`).join('')}</ul>` : '')}</div>`;
}

const PRINT_CSS = `@page{size:letter;margin:0.75in}body{font-family:Calibri,'Segoe UI',Arial,sans-serif;color:#111827;font-size:10.5pt;line-height:1.35;margin:0}
.rv h1{font-size:18pt;margin:0 0 2px;color:#1E293B}.rv .hl{color:#475569;font-size:11.5pt}.rv .ct{color:#475569;font-size:10pt;margin:2px 0 6px}
.rv h2{font-size:11pt;text-transform:uppercase;letter-spacing:.4px;border-bottom:1px solid #CBD5E1;margin:14px 0 6px;padding-bottom:2px;color:#1E293B;break-after:avoid}
.rv p{margin:0 0 4px}.rv ul{margin:2px 0 4px 18px;padding:0}.rv li{margin:0 0 2px}.rv .job{margin-top:8px;break-inside:avoid-page}
.rv .jh{display:flex;justify-content:space-between;gap:12px}.rv .jh span{color:#475569;white-space:nowrap}.rv .loc,.rv .env{color:#475569;font-size:10pt}
.rv .raw{white-space:pre-wrap}`;
