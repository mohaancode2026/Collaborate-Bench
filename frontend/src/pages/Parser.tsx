import { DragEvent, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, Consultant, Match } from '../api';
import { ConsultantDraft, ConsultantForm, emptyDraft, toInput } from '../components';
import { AiBadge, Alert, Empty, Field, FieldErrors, Spinner, useErrorHandler, useToast } from '../ui';
import { fileToText, storeResumeFile } from '../resumeFile';

interface Parsed {
  name: string; title: string; email: string; phone: string; location: string; visa: string; exp: number; rate?: number | null;
  skills: string[]; summary: string; relocate: boolean; source: string; notice?: string; duplicateWarning?: string; topMatches: Match[];
  linkedInUrl: string; passport: string; education: string; certifications: string; availability: string;
}

export default function Parser() {
  const nav = useNavigate();
  const notify = useToast();
  const onError = useErrorHandler();
  const [text, setText] = useState('');
  const [fileName, setFileName] = useState<string>();
  /** The uploaded file, kept so it's stored with the profile when it's added to the bench. */
  const [file, setFile] = useState<File | null>(null);
  const [reading, setReading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [parsed, setParsed] = useState<Parsed | null>(null);
  const [draft, setDraft] = useState<ConsultantDraft | null>(null);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [over, setOver] = useState(false);

  const load = async (f: File) => {
    setReading(true); setFileName(f.name);
    try { setText((await fileToText(f)).trim()); setFile(f); notify('success', 'Resume loaded', `Extracted text from ${f.name}.`); }
    catch (e) { setFile(null); notify('warning', 'Could not read that file', (e as Error).message || 'Try another file or paste the text.'); }
    finally { setReading(false); }
  };

  const run = async () => {
    setBusy(true); setErrors({}); setParsed(null);
    try {
      const p = await api<Parsed>('/ai/parse-resume', { method: 'POST', body: { text, fileName } });
      setParsed(p);
      setDraft({ ...emptyDraft(), name: p.name, title: p.title, email: p.email, phone: p.phone, visa: p.visa, location: p.location, relocate: p.relocate,
        rate: p.rate ?? '', exp: p.exp || '', status: 'Bench', skills: p.skills.join(', '), summary: p.summary,
        linkedInUrl: p.linkedInUrl ?? '', passportNumber: p.passport ?? '', education: p.education ?? '', certifications: p.certifications ?? '',
        availability: p.availability ?? '' });
      if (p.notice) notify('info', 'Built-in engine used', p.notice);
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };

  const save = async (openMatches: boolean) => {
    if (!draft || !parsed) return;
    setBusy(true); setErrors({});
    try {
      const c = await api<Consultant>('/consultants', { method: 'POST', body: { ...toInput(draft), resumeText: text, resumeFileName: fileName, parsedBy: parsed.source } });
      // Keep the resume file itself with the new profile, so it can be attached to submissions as-is.
      const problem = file ? await storeResumeFile(c.id, file) : null;
      notify(problem ? 'warning' : 'success', `${c.name} added to bench`,
        problem ? `Profile saved, but the resume file couldn't be stored: ${problem}` : file ? `Profile and resume file (${file.name}) saved, ready for matching.` : 'Profile saved and ready for matching.');
      nav(openMatches ? `/external-jobs?consultant=${c.id}` : '/bench');
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };

  return (
    <>
      <div className="ph"><div><h1>AI Resume Parser</h1><p>Upload or paste a resume. The parsing agent extracts the profile and checks for duplicates.</p></div></div>
      <div className="grid g12">
        <div className="card"><div className="card-h"><h3>1 · Resume input</h3></div><div className="card-b">
          <label className={`drop${over ? ' over' : ''}`} onDragOver={(e: DragEvent) => { e.preventDefault(); setOver(true); }} onDragLeave={() => setOver(false)}
            onDrop={(e: DragEvent) => { e.preventDefault(); setOver(false); const f = e.dataTransfer.files[0]; if (f) load(f); }}>
            {reading ? <Spinner dark /> : <svg width="34" height="34" fill="none" stroke="#2563eb" strokeWidth="1.8" viewBox="0 0 24 24"><path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4M17 8l-5-5-5 5M12 3v12" /></svg>}
            <b>{fileName ?? 'Drop a resume here or click to browse'}</b><small>PDF, DOCX or TXT · max 10 MB · read in your browser</small>
            <input type="file" hidden accept=".pdf,.docx,.txt,.md" onChange={e => { const f = e.target.files?.[0]; if (f) load(f); }} />
          </label>
          <Field label="…or paste resume text" error={errors.text} className="mt"><textarea className="inp" style={{ minHeight: 220 }} value={text} onChange={e => { setText(e.target.value); if (!e.target.value) setFile(null); }} placeholder="Paste the full resume here" /></Field>
          <button className="btn btn-dark" style={{ width: '100%', justifyContent: 'center' }} onClick={run} disabled={busy || reading}>{busy && !parsed ? <><Spinner /> Agent working…</> : '✦ Run parsing agent'}</button>
        </div></div>
        <div className="card"><div className="card-h"><h3>2 · Review &amp; save</h3><AiBadge source={parsed?.source} /></div><div className="card-b">
          {!parsed || !draft ? <Empty>The parsed fields show up here for you to review before saving.</Empty> : (
            <>
              {parsed.duplicateWarning && <Alert type="warning" title="Possible duplicate profile">{parsed.duplicateWarning}</Alert>}
              <ConsultantForm draft={draft} set={setDraft} errors={errors} />
              <div className="flex"><button className="btn btn-dark" onClick={() => save(false)} disabled={busy}>+ Add to bench</button><button className="btn btn-outline" onClick={() => save(true)} disabled={busy}>Add &amp; open matches</button></div>
            </>
          )}
        </div></div>
      </div>
    </>
  );
}
