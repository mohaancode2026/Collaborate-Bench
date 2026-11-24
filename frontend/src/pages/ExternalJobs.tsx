import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, Consultant, ExternalJob, ExternalSearchResult, JobFeedStatus, Match, Requirement, Submission } from '../api';
import { useAuth } from '../auth';
import { suggest } from '../components';
import { ComposeDialog } from '../compose';
import { AiText } from '../api';
import { Alert, Avatar, Empty, Field, FieldErrors, Modal, Ring, Spinner, ago, copyText, scoreClass, useErrorHandler, useToast } from '../ui';

const DATES: [string, string][] = [['today', 'Today'], ['3days', 'Last 3 days'], ['week', 'Last 7 days'], ['month', 'Last 30 days'], ['all', 'Any time']];
const TYPES = ['C2C', 'W2', '1099'];
const TIERS = ['Direct', 'Prime vendor', 'Implementation partner', 'Tier-1', 'Tier-2'];

const posted = (j: ExternalJob) => (j.daysOld == null ? 'Post date not shown' : j.daysOld === 0 ? 'Posted today' : j.daysOld === 1 ? 'Posted 1 day ago' : `Posted ${j.daysOld} days ago`);
const rateText = (j: ExternalJob) => !j.maxRate ? null
  : j.minRate && Math.round(j.minRate) !== Math.round(j.maxRate) ? `$${Math.round(j.minRate)}–$${Math.round(j.maxRate)}/hr` : `$${Math.round(j.maxRate)}/hr`;
const via = (j: ExternalJob) => j.publisher || 'the job board';
const skillList = (s: string) => s.split(',').map(x => x.trim()).filter(Boolean);
const MAX_SKILLS = 5;

/** LinkedIn first, then the employer's own site, then the rest. */
const applyLinks = (j: ExternalJob) => [...j.applyOptions].sort((a, b) =>
  Number(/linkedin/i.test(b.publisher)) - Number(/linkedin/i.test(a.publisher)) || Number(b.isDirect) - Number(a.isDirect)).slice(0, 4);

function ApplyLinks({ job, primary }: { job: ExternalJob; primary?: boolean }) {
  return <>{applyLinks(job).map((o, i) => (
    <a key={o.url} className={`btn ${primary && i === 0 ? 'btn-dark' : 'btn-outline'}`} href={o.url} target="_blank" rel="noopener noreferrer">
      Apply on {o.publisher}{o.isDirect ? ' (company site)' : ''} ↗</a>))}</>;
}

type View = { searchId?: number; consultantId?: number; label?: string; cached?: boolean; found?: number; added?: number };

export default function ExternalJobs() {
  const { can, scope } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const [status, setStatus] = useState<JobFeedStatus | null>(null);
  const [cons, setCons] = useState<Consultant[]>([]);
  const [form, setForm] = useState({ mode: 'skills' as 'skills' | 'keyword', consultantId: '', skills: [] as string[], includeTitle: false,
    query: '', location: '', datePosted: 'week', contractOnly: true, remoteOnly: false });
  const [errors, setErrors] = useState<FieldErrors>({});
  const [searching, setSearching] = useState(false);
  const [view, setView] = useState<View>({});
  const [list, setList] = useState<ExternalJob[] | null>(null);
  const [filter, setFilter] = useState({ q: '', minScore: '', hidden: false, board: '' });
  const [sel, setSel] = useState<number | null>(null);
  const [best, setBest] = useState<Match[] | null>(null);
  const [apply, setApply] = useState<{ job: ExternalJob; consultantId?: number } | null>(null);
  const [importJob, setImportJob] = useState<ExternalJob | null>(null);
  const detailRef = useRef<HTMLElement>(null);

  const loadStatus = useCallback(() => { api<JobFeedStatus>('/external-jobs/status').then(setStatus).catch(onError); }, [onError]);
  useEffect(loadStatus, [loadStatus]);
  useEffect(() => {
    api<Consultant[]>('/consultants', { query: { scope } }).then(c => setCons(c.filter(x => x.status === 'Bench' || x.status === 'Marketing'))).catch(onError);
  }, [scope, onError]);

  const load = useCallback(() => {
    setList(null);
    api<ExternalJob[]>('/external-jobs', { query: { searchId: view.searchId, consultantId: view.consultantId, hidden: filter.hidden || undefined, board: filter.board || undefined, scope } })
      .then(r => { setList(r); setSel(s => (r.some(x => x.id === s) ? s : r[0]?.id ?? null)); })
      .catch(e => { setList([]); onError(e); });
  }, [view.searchId, view.consultantId, filter.hidden, filter.board, scope, onError]);
  useEffect(load, [load]);

  useEffect(() => {
    if (!sel || !can('match.view')) { setBest(null); return; }
    setBest(null);
    api<Match[]>(`/external-jobs/${sel}/matches`, { query: { scope } }).then(setBest).catch(() => setBest([]));
  }, [sel, scope, can, list]);

  const shown = useMemo(() => {
    const q = filter.q.trim().toLowerCase(), min = Number(filter.minScore) || 0;
    return (list ?? []).filter(j => j.topScore >= min && (!q || `${j.title} ${j.company} ${j.location} ${j.publisher ?? ''} ${j.skills.join(' ')}`.toLowerCase().includes(q)));
  }, [list, filter.q, filter.minScore]);
  const job = list?.find(j => j.id === sel);
  const chosen = form.mode === 'skills' ? cons.find(c => String(c.id) === form.consultantId) : undefined;
  const pickConsultant = (id: string) => {
    const c = cons.find(x => String(x.id) === id);
    setForm({ ...form, consultantId: id, skills: c ? c.skills.slice(0, 3) : [] });
  };
  const toggleSkill = (s: string) => setForm(f => ({ ...f, skills: f.skills.includes(s) ? f.skills.filter(x => x !== s) : f.skills.length >= MAX_SKILLS ? f.skills : [...f.skills, s] }));
  useEffect(() => { if (form.mode === 'skills' && !form.consultantId && cons[0]) pickConsultant(String(cons[0].id)); }, [cons, form.mode]); // eslint-disable-line react-hooks/exhaustive-deps

  const search = async (e: FormEvent) => {
    e.preventDefault();
    setSearching(true); setErrors({});
    try {
      const bySkills = form.mode === 'skills' && !!chosen;
      const r = await api<ExternalSearchResult>('/external-jobs/search', { method: 'POST', body: bySkills
        ? { consultantId: chosen!.id, skills: form.skills, includeTitle: form.includeTitle, location: form.location,
            remoteOnly: form.remoteOnly, datePosted: form.datePosted, contractOnly: form.contractOnly }
        : { query: form.query, location: form.location, remoteOnly: form.remoteOnly, datePosted: form.datePosted, contractOnly: form.contractOnly } });
      setView({ searchId: r.searchId, consultantId: bySkills ? chosen!.id : undefined,
        label: `“${r.query}”${r.location ? ` in ${r.location}` : ''}${chosen ? ` for ${chosen.name}` : ''}`, cached: r.cached, found: r.found, added: r.new });
      setFilter(f => ({ ...f, hidden: false }));
      notify(r.found ? 'success' : 'info', r.found ? `${r.found} job${r.found === 1 ? '' : 's'} found` : 'No jobs found',
        r.cached ? 'Same search was run recently, so saved results were used (no API call).' : r.found ? `${r.new} new since earlier searches.` : 'Try a broader title, another location, or a longer date range.');
    } catch (err) { onError(err, setErrors); }
    finally { setSearching(false); loadStatus(); }
  };

  const toggleHidden = async (j: ExternalJob) => {
    try {
      const r = await api<{ hidden: boolean }>(`/external-jobs/${j.id}/hide`, { method: 'POST' });
      notify('success', r.hidden ? 'Job hidden' : 'Job restored', r.hidden ? 'It won\'t show in results. Tick "Show hidden jobs" to find it again.' : undefined);
      load();
    } catch (e) { onError(e); }
  };

  const select = (id: number) => {
    setSel(id);
    if (window.matchMedia('(max-width: 1000px)').matches) setTimeout(() => detailRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50);
  };

  // Only JSearch is metered; Dice is free.
  const quota = status?.indeed ? `${status.callsToday} of ${status.dailyLimit} Indeed / LinkedIn searches used today` : null;
  const boards = [...(status?.dice ? ['Dice'] : []), ...(status?.indeed ? ['Indeed', 'LinkedIn', 'Monster', 'Glassdoor'] : [])];

  return (
    <>
      <div className="ph"><div><h1>External Jobs</h1>
        <p>Live jobs from {boards.length ? boards.join(', ') : 'job boards'}, scored against your bench. Apply a candidate in one step.</p></div>
        {quota && <span className="tag s" title="The JSearch limit is shared by the whole company and resets at midnight UTC. Dice searches don't count.">{quota}</span>}</div>

      {status && !status.configured && (
        <Alert type="info" title="Live job search isn't set up yet">
          Ask your admin to turn on Dice (<span className="mono">JobFeed:DiceEnabled</span>) or add a JSearch key from RapidAPI (<span className="mono">JobFeed:RapidApiKey</span>) and restart the API.
          Jobs saved from earlier searches still work below.
        </Alert>
      )}
      {status?.configured && status.dice && !status.indeed && (
        <Alert type="info" title="Searching Dice only">
          To include Indeed, LinkedIn, Monster and Glassdoor jobs too, ask your admin to add a JSearch key from RapidAPI (<span className="mono">JobFeed:RapidApiKey</span>).
        </Alert>
      )}

      <div className="tabs" style={{ marginBottom: 10, width: 'fit-content' }}>
        <button className={form.mode === 'skills' ? 'on' : ''} onClick={() => setForm({ ...form, mode: 'skills' })}>Match a consultant's skills</button>
        <button className={form.mode === 'keyword' ? 'on' : ''} onClick={() => setForm({ ...form, mode: 'keyword' })}>Keyword search</button>
      </div>
      <form className={`xsearch${form.mode === 'keyword' ? ' k' : ''}`} onSubmit={search}>
        {form.mode === 'skills'
          ? <select className="inp" aria-label="Consultant" value={form.consultantId} onChange={e => pickConsultant(e.target.value)}>
              {cons.length === 0 && <option value="">No bench consultants in your view</option>}
              {cons.map(c => <option key={c.id} value={c.id}>{c.name} · {c.title || c.skills.slice(0, 2).join(', ')}</option>)}
            </select>
          : <input className="inp" aria-label="Job title or skills" value={form.query} onChange={e => setForm({ ...form, query: e.target.value })}
              placeholder="Job title or skills, e.g. Java Spring Boot Kafka" />}
        <input className="inp" aria-label="Location" value={form.location} onChange={e => setForm({ ...form, location: e.target.value })}
          placeholder={chosen ? `${chosen.location || 'Remote'} (from profile)` : 'City, ST (blank = anywhere)'} />
        <button className="btn btn-light" type="submit"
          disabled={searching || status?.configured === false || (form.mode === 'skills' && (!chosen || (form.skills.length === 0 && !form.includeTitle)))}>
          {searching ? <Spinner dark /> : form.mode === 'skills' ? 'Find matching jobs' : 'Search jobs'}</button>
      </form>
      {chosen && (
        <div className="flex" style={{ marginBottom: 10 }}>
          <span className="muted" style={{ fontSize: 13 }}>Search with skills (up to {MAX_SKILLS}):</span>
          {chosen.skills.map(s => <button key={s} type="button" className={`skchip${form.skills.includes(s) ? ' on' : ''}`} aria-pressed={form.skills.includes(s)} onClick={() => toggleSkill(s)}>{s}</button>)}
          {chosen.skills.length === 0 && <span className="muted" style={{ fontSize: 13 }}>No skills on this profile. Tick "include job title" or add skills on the bench.</span>}
          {chosen.title && <label className="flex" style={{ fontSize: 13, marginLeft: 6 }}><input type="checkbox" checked={form.includeTitle} onChange={e => setForm({ ...form, includeTitle: e.target.checked })} />Include job title ({chosen.title})</label>}
        </div>)}
      {(errors.query || errors.location) && <p className="ferr" style={{ margin: '-4px 0 10px', color: 'var(--bad)', fontSize: 13 }}>{[...(errors.query ?? []), ...(errors.location ?? [])].join(' ')}</p>}
      <div className="xopts">
        <select className="inp" style={{ width: 'auto' }} aria-label="Posted" value={form.datePosted} onChange={e => setForm({ ...form, datePosted: e.target.value })}>
          {DATES.map(([v, t]) => <option key={v} value={v}>{t}</option>)}</select>
        <label className="flex"><input type="checkbox" checked={form.contractOnly} onChange={e => setForm({ ...form, contractOnly: e.target.checked })} />Contract jobs only</label>
        <label className="flex"><input type="checkbox" checked={form.remoteOnly} onChange={e => setForm({ ...form, remoteOnly: e.target.checked })} />Remote only</label>
        <span className="muted" style={{ fontSize: 12.5 }}>Fewer skills find more jobs. Searches send only skills, title and location, never consultant names or contact details.
          {status?.dice && ' Dice listings come from Dice\'s AI-powered job search: check the details with the employer before submitting.'}</span>
      </div>

      <div className="rescount">
        <span>{view.searchId
          ? <>Results for {view.label}{view.cached ? ' (saved results)' : ''} · <button className="btn btn-sm" style={{ color: 'var(--primary)', padding: 0 }} onClick={() => setView({})}>Show all saved jobs</button></>
          : <>All saved jobs from the last 30 days{status?.lastSearchUtc ? ` · last search ${ago(status.lastSearchUtc)}` : ''}</>}</span>
        <span className="flex">
          <input className="inp" style={{ width: 170, padding: '6px 10px' }} placeholder="Filter results" aria-label="Filter results" value={filter.q} onChange={e => setFilter({ ...filter, q: e.target.value })} />
          <select value={filter.minScore} onChange={e => setFilter({ ...filter, minScore: e.target.value })} aria-label="Minimum match">
            {[['', 'Any match'], ['50', '50%+ match'], ['60', '60%+ match'], ['75', '75%+ match']].map(([v, t]) => <option key={v} value={v}>{t}</option>)}</select>
          <select value={filter.board} onChange={e => setFilter({ ...filter, board: e.target.value })} aria-label="Job board">
            {[['', 'All job boards'], ['dice', 'Dice'], ['indeed', 'Indeed'], ['linkedin', 'LinkedIn'], ['monster', 'Monster']].map(([v, t]) => <option key={v} value={v}>{t}</option>)}</select>
          <label className="flex" style={{ fontSize: 13 }}><input type="checkbox" checked={filter.hidden} onChange={e => setFilter({ ...filter, hidden: e.target.checked })} />Show hidden jobs</label>
        </span>
      </div>

      <div className="xboard">
        <section>
          {list === null ? <Empty><Spinner dark /> Loading jobs…</Empty> : shown.map(j => (
            <div key={j.id} className={`jcard${j.id === sel ? ' sel' : ''}`} tabIndex={0} onClick={() => select(j.id)} onKeyDown={e => { if (e.key === 'Enter') select(j.id); }}>
              <div className="flex between" style={{ flexWrap: 'nowrap' }}>
                <div className="co"><Avatar name={j.company} />{j.company}</div>
                {j.matches[0] && <span className={`tag ${scoreClass(j.topScore)}`} title={`Best match: ${j.matches[0].consultantName}`}>{j.topScore}% · {j.matches[0].consultantName.split(' ')[0]}</span>}
              </div>
              <h4>{j.title}</h4>
              <div className="meta"><span>{j.location || '—'}</span><i>•</i><span>{j.mode}</span><i>•</i><span>{posted(j)}</span></div>
              <div className="flex" style={{ marginTop: 10 }}>
                {j.onLinkedIn ? <span className="tag li">On LinkedIn</span> : <span className="tag s">via {via(j)}</span>}
                {view.consultantId && j.matches[0] && j.skills.length > 0 && <span className={`tag ${scoreClass(Math.round(j.matches[0].hit.length / j.skills.length * 100))}`}>Skills {j.matches[0].hit.length}/{j.skills.length}</span>}
                {rateText(j) && <span className="tag mono">{rateText(j)}</span>}
                {j.requirementId && <span className="tag g">Imported</span>}
              </div>
            </div>))}
          {list !== null && shown.length === 0 && (
            <div className="card empty">
              <b>{list.length === 0 ? (view.searchId ? 'This search found no jobs.' : 'No saved jobs yet.') : 'No jobs match these filters.'}</b>
              <div>{list.length === 0 ? (status?.configured ? 'Pick a consultant or type a job title above and search.' : 'Once job search is set up, results will appear here.') : 'Clear the filter or lower the minimum match.'}</div>
            </div>)}
        </section>

        <aside className="card xdetail" ref={detailRef}>{!job ? <Empty>Select a job to see details</Empty> : (
          <div className="card-b">
            <div className="flex" style={{ fontWeight: 600, color: 'var(--ink-2)' }}><Avatar name={job.company} /><div>{job.company}<div className="muted" style={{ fontSize: 12, fontWeight: 500 }}>Found on {via(job)} via {job.provider}</div></div></div>
            <h2 style={{ fontSize: 20, margin: '12px 0 6px' }}>{job.title}</h2>
            <div className="muted" style={{ fontSize: 13 }}>{job.location || '—'} · {job.mode} · {posted(job)}</div>
            <div className="flex mt">
              {job.employmentType && <span className="tag v">{job.employmentType}</span>}
              <span className="tag mono">{rateText(job) ?? 'Rate not posted'}</span>
              <span className="tag s" title="Read from the job description. Confirm with the vendor or employer.">Visa: {job.visas.join(', ')}</span>
            </div>
            <div className="flex mt">
              {can('sub.create') && <button className="btn btn-primary" onClick={() => setApply({ job })}>Apply candidate</button>}
              <ApplyLinks job={job} />
              {job.requirementId
                ? (can('match.view') && <Link className="btn btn-outline" to={`/match?requirement=${job.requirementId}`}>Imported · open in Match Agent</Link>)
                : can('jobs.create') && <button className="btn btn-outline" onClick={() => setImportJob(job)}>Import as requirement</button>}
              {can('jobs.create') && <button className="btn btn-sm" style={{ color: 'var(--muted)' }} onClick={() => toggleHidden(job)}>{job.hidden ? 'Restore' : 'Hide'}</button>}
            </div>

            {can('match.view') && <div className="card mt2" style={{ background: '#f8fafc' }}><div className="card-b" style={{ padding: 14 }}>
              <b style={{ fontSize: 13 }}>Best matches in your view</b>
              {best === null ? <div className="muted mt"><Spinner dark /> Scoring…</div> : best.slice(0, 5).map(m => (
                <div key={m.consultantId} className="flex between" style={{ padding: '8px 0', borderBottom: '1px solid var(--line)', flexWrap: 'nowrap' }}>
                  <div className="who" style={{ minWidth: 0 }}><Avatar name={m.consultantName} size={28} />
                    <div><b style={{ fontSize: 13 }}>{m.consultantName}</b><small title={m.why}>{m.visa || 'Visa ?'} · ${m.payRate}/hr · {m.ownerName}</small></div></div>
                  <span className="flex" style={{ flexWrap: 'nowrap' }}>
                    <span className={`tag ${scoreClass(m.score)}`}>{m.score}%</span>
                    {m.submittedStage ? <span className="tag c">{m.submittedStage}</span>
                      : can('sub.create') && <button className="btn btn-sm btn-outline" onClick={() => setApply({ job, consultantId: m.consultantId })}>Apply</button>}
                  </span>
                </div>))}
              {best?.length === 0 && <div className="muted mt">No bench consultants in your view.</div>}
            </div></div>}

            {job.contactEmails.length > 0 && <p className="muted mt" style={{ fontSize: 13 }}>Recruiter email in the posting: {job.contactEmails.join(', ')}. You can email the candidate from the apply screen.</p>}
            <h4 className="mt2" style={{ fontSize: 14 }}>Skills found in the posting</h4>
            <div style={{ marginTop: 6 }}>{job.skills.length ? job.skills.map(s => <span key={s} className="tag">{s}</span>) : <span className="muted">None recognised. Add them when you import.</span>}</div>
            {job.description && <><h4 className="mt2" style={{ fontSize: 14 }}>Job description</h4><div className="jd" style={{ marginTop: 8 }}>{job.description}</div></>}
          </div>)}
        </aside>
      </div>

      {apply && <ApplyDialog job={apply.job} consultantId={apply.consultantId} onClose={() => setApply(null)} onDone={load} />}
      {importJob && <ImportDialog job={importJob} onClose={() => setImportJob(null)} onDone={load} />}
    </>
  );
}

// ---------- Import fields shared by both dialogs ----------
interface ImportDraft { billRate: number | ''; type: string; vendor: string; tier: string; skills: string; }
const importDraft = (j: ExternalJob): ImportDraft => ({ billRate: j.maxRate ? Math.round(j.maxRate) : '', type: j.suggestedType, vendor: '', tier: 'Direct', skills: j.skills.join(', ') });
const importBody = (d: ImportDraft, j: ExternalJob) => ({ billRate: Number(d.billRate) || 0, type: d.type, vendor: d.vendor, tier: d.tier, skills: skillList(d.skills), visas: j.visas });

function ImportFields({ d, set, errors, job }: { d: ImportDraft; set: (d: ImportDraft) => void; errors: FieldErrors; job: ExternalJob }) {
  return (
    <>
      <div className="row3">
        <Field label="Bill rate ($/hr) *" error={errors.billRate}>
          <input className="inp" type="number" value={d.billRate} placeholder={job.maxRate ? undefined : 'Not posted'} onChange={e => set({ ...d, billRate: e.target.value === '' ? '' : Number(e.target.value) })} /></Field>
        <Field label="Contract type *" error={errors.type}>
          <select className="inp" value={d.type} onChange={e => set({ ...d, type: e.target.value })}>{TYPES.map(t => <option key={t}>{t}</option>)}</select></Field>
        <Field label="Vendor tier" error={errors.tier}>
          <select className="inp" value={d.tier} onChange={e => set({ ...d, tier: e.target.value })}>{TIERS.map(t => <option key={t}>{t}</option>)}</select></Field>
      </div>
      <Field label="Vendor (who you submit to)" error={errors.vendor}>
        <input className="inp" value={d.vendor} placeholder="Direct client" onChange={e => set({ ...d, vendor: e.target.value })} /></Field>
      <Field label="Required skills * (comma separated)" error={errors.skills}>
        <input className="inp" value={d.skills} onChange={e => set({ ...d, skills: e.target.value })} /></Field>
      {!job.maxRate && <p className="muted" style={{ fontSize: 12.5, marginTop: -6, marginBottom: 12 }}>{via(job)} didn't post a rate. Use the rate the vendor quoted, or your best estimate.</p>}
    </>
  );
}

function ImportDialog({ job, onClose, onDone }: { job: ExternalJob; onClose: () => void; onDone: () => void }) {
  const notify = useToast();
  const onError = useErrorHandler();
  const [d, setD] = useState(() => importDraft(job));
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setErrors({});
    try {
      const r = await api<{ requirement: Requirement; linkedExisting: boolean }>(`/external-jobs/${job.id}/import`, { method: 'POST', body: importBody(d, job) });
      notify('success', r.linkedExisting ? 'Linked to an existing requirement' : 'Added to requirements',
        r.linkedExisting ? `"${r.requirement.title}" was already open, so the job was linked to it.` : `"${r.requirement.title}" is now on the job board and in the Match Agent.`);
      onDone(); onClose();
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };
  return (
    <Modal title={`Import · ${job.title}`} onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save} disabled={busy}>{busy ? <Spinner /> : 'Import as requirement'}</button></>}>
      <p className="muted" style={{ marginBottom: 12 }}>Creates an open requirement for {job.company} so your team can match and submit to it like any other job.</p>
      <ImportFields d={d} set={setD} errors={errors} job={job} />
    </Modal>
  );
}

// ---------- Apply a candidate ----------
function ApplyDialog({ job, consultantId, onClose, onDone }: { job: ExternalJob; consultantId?: number; onClose: () => void; onDone: () => void }) {
  const { can } = useAuth();
  const notify = useToast();
  const onError = useErrorHandler();
  const [matches, setMatches] = useState<Match[] | null>(null);
  const [pick, setPick] = useState<Match | null>(null);
  const [req, setReq] = useState<Requirement | null>(null);
  const [d, setD] = useState(() => importDraft(job));
  const [submitRate, setSubmitRate] = useState<number | ''>('');
  const [touched, setTouched] = useState(false);
  const [payRate, setPayRate] = useState<number | ''>('');
  const [notes, setNotes] = useState('');
  const [rtr, setRtr] = useState(false);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<{ submission: Submission; requirementId: number } | null>(null);
  const [pitch, setPitch] = useState<string | null>(null);
  const [emailTo, setEmailTo] = useState<string | null>(null);

  useEffect(() => {
    const src = can('match.view') ? api<Match[]>(`/external-jobs/${job.id}/matches`) : Promise.resolve(job.matches);
    src.then(ms => { setMatches(ms); const m = ms.find(x => x.consultantId === consultantId && !x.submittedStage); if (m) choose(m); })
      .catch(e => { onError(e); onClose(); });
    if (job.requirementId) api<Requirement>(`/requirements/${job.requirementId}`).then(setReq).catch(() => setReq(null));
  }, [job.id]); // eslint-disable-line react-hooks/exhaustive-deps

  const bill = req ? req.rate : Number(d.billRate) || 0;
  const choose = (m: Match) => { setPick(m); setPayRate(m.payRate); setTouched(false); };
  useEffect(() => { if (pick && !touched) setSubmitRate(bill ? suggest(Number(payRate) || 0, bill) : ''); }, [pick, bill, payRate, touched]);

  const sr = Number(submitRate), pr = Number(payRate);
  const rateMsg = bill && sr > bill ? `Above the $${bill}/hr bill rate` : sr && pr && sr <= pr ? 'Must be above the pay rate'
    : can('margin.view') && sr && pr ? `Margin $${(sr - pr).toFixed(2)}/hr (${Math.round((sr - pr) / sr * 100)}%)` : '';

  const submit = async () => {
    if (!pick) return;
    setBusy(true); setErrors({});
    try {
      const r = await api<{ submission: Submission; warnings: string[]; requirementId: number }>(`/external-jobs/${job.id}/apply`, { method: 'POST', body: {
        consultantId: pick.consultantId, submitRate: sr, payRate: pr, rtrSigned: rtr, notes, ...importBody(d, job) } });
      notify('success', 'Candidate submitted', `${r.submission.consultantName} → ${r.submission.title} (${r.submission.client})`);
      r.warnings.forEach(w => notify('warning', 'Heads up', w));
      setDone(r); onDone();
      api<AiText>('/ai/pitch', { method: 'POST', body: { consultantId: r.submission.consultantId, requirementId: r.requirementId } })
        .then(p => setPitch(p.text)).catch(() => setPitch(''));
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };

  if (done) return (
    <Modal title="Candidate submitted" onClose={onClose}
      footer={<><Link className="btn btn-outline" to="/pipeline" onClick={onClose}>View in Submissions</Link><button className="btn btn-primary" onClick={onClose}>Done</button></>}>
      <Alert type="success" title={`${done.submission.consultantName} is in the pipeline at "Submitted".`}>
        The job is now a requirement, so stages, onboarding and reports track it like any other submission.
      </Alert>
      <p className="mt"><b>Last step:</b> LinkedIn and other job boards only take applications on their own site. Copy the cover note, then apply there with the resume.</p>
      <Field label="Cover note (edit before you paste)" className="mt">
        {pitch === null ? <div className="muted"><Spinner dark /> Writing a cover note…</div>
          : <textarea className="inp" style={{ minHeight: 150 }} value={pitch} onChange={e => setPitch(e.target.value)} placeholder="Write a short note about the candidate" />}
      </Field>
      <div className="flex">
        {pitch && <button className="btn btn-outline" onClick={() => copyText(pitch, notify)}>Copy cover note</button>}
        <ApplyLinks job={job} primary />
        {job.contactEmails.map(e => <button key={e} className="btn btn-outline" onClick={() => setEmailTo(e)}>✉ Email {e}</button>)}
      </div>
      {job.contactEmails.length > 0 && <p className="muted" style={{ fontSize: 12.5, marginTop: 8 }}>Sends from your Outlook with the cover note filled in. Attach the resume in the email window.</p>}
      {emailTo && <ComposeDialog onClose={() => setEmailTo(null)}
        initial={{ to: emailTo, subject: `${done.submission.consultantTitle || 'Candidate'}: ${done.submission.consultantName} for ${job.title}`, body: pitch ?? '' }} />}
    </Modal>
  );

  return (
    <Modal title={`Apply candidate · ${job.title}`} onClose={onClose}
      footer={<>{pick && !consultantId && <button className="btn btn-outline" style={{ marginRight: 'auto' }} onClick={() => setPick(null)}>← Choose someone else</button>}
        <button className="btn btn-outline" onClick={onClose}>Cancel</button>
        {pick && <button className="btn btn-primary" onClick={submit} disabled={busy || (!req && !can('jobs.create'))}>{busy ? <Spinner /> : 'Submit candidate'}</button>}</>}>
      {matches === null ? <Empty><Spinner dark /> Scoring your bench…</Empty> : !pick ? (
        <>
          <p className="muted" style={{ marginBottom: 12 }}>Choose a consultant from your view. Best matches are first.</p>
          {matches.map(m => (
            <button key={m.consultantId} className="match" disabled={!!m.submittedStage} style={{ width: '100%', textAlign: 'left', cursor: m.submittedStage ? 'default' : 'pointer', opacity: m.submittedStage ? 0.6 : 1 }}
              onClick={() => choose(m)}>
              <Ring score={m.score} size={46} />
              <div className="mi"><b>{m.consultantName}</b><small>{m.consultantTitle} · {m.visa || 'Visa ?'} · ${m.payRate}/hr</small><div className="why">{m.why}</div></div>
              {m.submittedStage && <span className="tag c">Already {m.submittedStage.toLowerCase()}</span>}
            </button>))}
          {matches.length === 0 && <Empty>No bench consultants in your view.</Empty>}
        </>
      ) : (
        <>
          <div className="flex" style={{ marginBottom: 14 }}><Avatar name={pick.consultantName} />
            <div><b>{pick.consultantName}</b> <span className={`tag ${scoreClass(pick.score)}`}>{pick.score}% match</span>
              <div className="muted" style={{ fontSize: 13 }}>→ {job.title} · {job.company} · found on {via(job)}</div></div></div>
          {!pick.visaOk && <Alert type="warning" title="Visa may not be accepted">{pick.visa || 'Unknown visa'} isn't in the visas read from the posting ({job.visas.join(', ')}). Confirm before applying.</Alert>}
          {pick.miss.length > 0 && <p className="muted" style={{ fontSize: 12.5, margin: '0 0 12px' }}>Skill gaps: {pick.miss.join(', ')}</p>}
          {req
            ? <p className="muted" style={{ fontSize: 13, marginBottom: 12 }}>Already imported as a requirement: {req.type} via {req.vendor}, ${req.rate}/hr bill rate.</p>
            : can('jobs.create')
              ? <ImportFields d={d} set={setD} errors={errors} job={job} />
              : <Alert type="warning" title="This job needs to be imported first">Your role can't post requirements. Ask your Team Lead to import it, then apply.</Alert>}
          <div className="row2">
            <Field label="Submit rate ($/hr) *" error={errors.submitRate}>
              <input className="inp" type="number" value={submitRate} onChange={e => { setTouched(true); setSubmitRate(e.target.value === '' ? '' : Number(e.target.value)); }} /></Field>
            <Field label="Consultant pay rate ($/hr) *" error={errors.payRate}>
              <input className="inp" type="number" value={payRate} onChange={e => setPayRate(e.target.value === '' ? '' : Number(e.target.value))} /></Field>
          </div>
          {rateMsg && <p style={{ fontSize: 12.5, margin: '-6px 0 12px', color: rateMsg.startsWith('Above') || rateMsg.startsWith('Must') ? 'var(--bad)' : 'var(--muted)' }}>{rateMsg}</p>}
          <Field label="Notes" error={errors.notes}><textarea className="inp" style={{ minHeight: 60 }} value={notes} onChange={e => setNotes(e.target.value)} placeholder="Availability, interview slots, recruiter contact at the employer…" /></Field>
          <Field error={errors.rtrSigned}>
            <label className="check"><input type="checkbox" checked={rtr} onChange={e => setRtr(e.target.checked)} />
              <span>{pick.consultantName} approved this application and signed the <b>Right to Represent (RTR)</b> for {job.company} *</span></label>
          </Field>
        </>
      )}
    </Modal>
  );
}
