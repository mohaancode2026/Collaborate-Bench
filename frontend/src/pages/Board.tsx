import { FormEvent, useCallback, useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, Match, Requirement } from '../api';
import { useAuth } from '../auth';
import { SubmitDialog } from '../components';
import { Avatar, Empty, Modal, Ring, Spinner, scoreClass, useErrorHandler, useToast } from '../ui';

const posted = (d: number) => (d === 0 ? 'Today' : d === 1 ? '1 day ago' : `${d} days ago`);

export default function Board() {
  const { can, scope } = useAuth();
  const [params] = useSearchParams();
  const nav = useNavigate();
  const onError = useErrorHandler();
  const notify = useToast();
  const [f, setF] = useState({ q: params.get('q') ?? '', location: params.get('location') ?? '', postedDays: '', mode: [] as string[], type: [] as string[], visa: '', minRate: '', savedOnly: false });
  const [list, setList] = useState<Requirement[] | null>(null);
  const [sel, setSel] = useState<number | null>(null);
  const [best, setBest] = useState<Match[]>([]);
  const [pick, setPick] = useState<Requirement | null>(null);
  const [submit, setSubmit] = useState<[number, number] | null>(null);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [sort, setSort] = useState('relevance');

  const load = useCallback(() => {
    api<Requirement[]>('/requirements', { query: { status: 'Open', q: f.q, location: f.location, postedDays: f.postedDays, mode: f.mode.join(','), type: f.type.join(','), visa: f.visa, minRate: f.minRate, savedOnly: f.savedOnly || undefined } })
      .then(r => { setList(r); setSel(s => (r.some(x => x.id === s) ? s : r[0]?.id ?? null)); }).catch(e => { setList([]); onError(e); });
  }, [f, onError]);
  useEffect(load, [load]);
  useEffect(() => {
    if (!sel || !can('match.view')) { setBest([]); return; }
    api<Match[]>(`/match/requirement/${sel}`, { query: { scope } }).then(setBest).catch(() => setBest([]));
  }, [sel, scope, can]);

  const sorted = [...(list ?? [])].sort((a, b) => sort === 'date' ? a.daysOld - b.daysOld : sort === 'rate' ? b.rate - a.rate : 0);
  const job = list?.find(j => j.id === sel);
  const toggleSave = async (id: number) => {
    try { const r = await api<{ saved: boolean }>(`/requirements/${id}/save`, { method: 'POST' }); notify('success', r.saved ? 'Job saved' : 'Removed from saved'); load(); } catch (e) { onError(e); }
  };
  const arr = (k: 'mode' | 'type', v: string, on: boolean) => setF({ ...f, [k]: on ? [...f[k], v] : f[k].filter(x => x !== v) });

  const detail = job && (
    <div className="card-b">
      <div className="flex" style={{ fontWeight: 600, color: 'var(--ink-2)' }}><Avatar name={job.client} /><div>{job.client}<div className="muted" style={{ fontSize: 12, fontWeight: 500 }}>via {job.vendor} · {job.tier}</div></div></div>
      <h2 style={{ fontSize: 20, margin: '12px 0 6px' }}>{job.title}</h2>
      <div className="muted" style={{ fontSize: 13 }}>{job.location} · {job.mode} · Posted {posted(job.daysOld).toLowerCase()} by {job.postedByName}</div>
      <div className="flex mt"><span className="tag v">Contract · {job.type}</span><span className="tag mono">${job.rate}/hr</span>{job.visas.map(v => <span key={v} className="tag s">{v}</span>)}</div>
      <div className="flex mt">{can('sub.create') && <button className="btn btn-primary" onClick={() => setPick(job)}>⚡ Quick Submit</button>}<button className="btn btn-outline" onClick={() => toggleSave(job.id)}>{job.saved ? '♥ Saved' : '♡ Save'}</button></div>
      {can('match.view') && <div className="card mt2" style={{ background: '#f8fafc' }}><div className="card-b" style={{ padding: 14 }}><b style={{ fontSize: 13 }}>Best matches in your view</b>
        {best.slice(0, 5).map(m => <div key={m.consultantId} className="flex between" style={{ padding: '8px 0', borderBottom: '1px solid var(--line)', flexWrap: 'nowrap' }}>
          <div className="who" style={{ minWidth: 0 }}><Avatar name={m.consultantName} size={28} /><div><b style={{ fontSize: 13 }}>{m.consultantName}</b><small>{m.visa} · ${m.payRate}/hr · {m.ownerName}</small></div></div>
          <span className={`tag ${scoreClass(m.score)}`}>{m.score}%</span></div>)}
        {best.length === 0 && <div className="muted mt">No consultants in your view.</div>}</div></div>}
      <h4 className="mt2" style={{ fontSize: 14 }}>Skills</h4><div style={{ marginTop: 6 }}>{job.skills.map(s => <span key={s} className="tag">{s}</span>)}</div>
      {job.description && <><h4 className="mt2" style={{ fontSize: 14 }}>Job description</h4><div className="jd" style={{ marginTop: 8 }}>{job.description}</div></>}
    </div>
  );

  return (
    <>
      <div className="ph"><div><h1>Job Board Search</h1><p>Search every open C2C, W2 and 1099 requirement and submit consultants from your view.</p></div>
        {can('jobs.create') && <button className="btn btn-dark" onClick={() => nav('/jobs?tab=post')}>+ Post a job</button>}</div>
      <form className="board-search" onSubmit={(e: FormEvent<HTMLFormElement>) => { e.preventDefault(); const d = new FormData(e.currentTarget); setF({ ...f, q: String(d.get('q') ?? ''), location: String(d.get('loc') ?? '') }); }}>
        <div className="hs-f"><input name="q" defaultValue={f.q} placeholder="Job title, skill, client" aria-label="Keyword" /></div>
        <div className="hs-f"><input name="loc" defaultValue={f.location} placeholder="Location or Remote" aria-label="Location" /></div>
        <button className="btn btn-light" type="submit">Search</button>
      </form>
      <div className="board">
        <aside className={`card filters${filtersOpen ? ' open' : ''}`}>
          <div className="flex between"><b>Filters</b><button className="btn btn-sm" style={{ color: 'var(--primary)', padding: 0 }} onClick={() => setF({ q: '', location: '', postedDays: '', mode: [], type: [], visa: '', minRate: '', savedOnly: false })}>Clear all</button></div>
          <h4 style={{ marginTop: 14 }}>Posted date</h4>
          {[['', 'Any time'], ['0', 'Today'], ['3', 'Last 3 days'], ['7', 'Last 7 days']].map(([v, t]) => <label key={v}><input type="radio" name="pd" checked={f.postedDays === v} onChange={() => setF({ ...f, postedDays: v })} />{t}</label>)}
          <h4>Work setting</h4>{['Remote', 'Hybrid', 'Onsite'].map(v => <label key={v}><input type="checkbox" checked={f.mode.includes(v)} onChange={e => arr('mode', v, e.target.checked)} />{v}</label>)}
          <h4>Employment type</h4>{[['C2C', 'Contract · Corp‑to‑Corp'], ['W2', 'Contract · W2'], ['1099', 'Contract · 1099']].map(([v, t]) => <label key={v}><input type="checkbox" checked={f.type.includes(v)} onChange={e => arr('type', v, e.target.checked)} />{t}</label>)}
          <h4>Work authorization</h4><select className="inp" value={f.visa} onChange={e => setF({ ...f, visa: e.target.value })}><option value="">Any visa</option>{['USC', 'GC', 'GC-EAD', 'H1B', 'H4-EAD', 'OPT', 'TN'].map(v => <option key={v}>{v}</option>)}</select>
          <h4>Minimum bill rate</h4><select className="inp" value={f.minRate} onChange={e => setF({ ...f, minRate: e.target.value })}>{['', '60', '70', '80', '90', '100'].map(v => <option key={v} value={v}>{v ? `$${v}+/hr` : 'Any rate'}</option>)}</select>
          <h4>More</h4><label><input type="checkbox" checked={f.savedOnly} onChange={e => setF({ ...f, savedOnly: e.target.checked })} />Saved jobs only</label>
        </aside>
        <section>
          <div className="rescount"><span>{list ? `${list.length} job${list.length === 1 ? '' : 's'} found` : ''}</span>
            <span className="flex"><button className="btn btn-sm btn-outline mfilter" onClick={() => setFiltersOpen(o => !o)}>Filters</button>
              <select value={sort} onChange={e => setSort(e.target.value)} aria-label="Sort"><option value="relevance">Sort: Relevance</option><option value="date">Sort: Newest</option><option value="rate">Sort: Highest rate</option></select></span></div>
          {list === null ? <Empty><Spinner dark /> Searching…</Empty> : sorted.map(j => (
            <div key={j.id} className={`jcard${j.id === sel ? ' sel' : ''}`} tabIndex={0} onClick={() => setSel(j.id)}>
              <button className={`sv${j.saved ? ' on' : ''}`} aria-label="Save job" onClick={e => { e.stopPropagation(); toggleSave(j.id); }}>{j.saved ? '♥' : '♡'}</button>
              <div className="co"><Avatar name={j.client} />{j.client}</div><h4>{j.title}</h4>
              <div className="meta"><span>{j.location}</span><i>•</i><span>{j.mode}</span><i>•</i><span>{posted(j.daysOld)}</span></div>
              <div className="flex" style={{ marginTop: 10 }}><span className="tag v">Contract · {j.type}</span><span className="tag mono">${j.rate}/hr</span></div>
            </div>))}
          {list?.length === 0 && <div className="card empty"><b>No jobs match these filters.</b><div>Try clearing filters or broadening the location.</div></div>}
        </section>
        <aside className="card jdetail">{detail ?? <Empty>Select a job to see details</Empty>}</aside>
      </div>
      {pick && (
        <Modal title={`Quick Submit · ${pick.title}`} onClose={() => setPick(null)}>
          <p className="muted" style={{ marginBottom: 12 }}>Choose a consultant from your view. You'll confirm the RTR and rate next.</p>
          {best.filter(m => !m.submittedStage).map(m => (
            <button key={m.consultantId} className="match" style={{ width: '100%', textAlign: 'left', cursor: 'pointer' }} onClick={() => { setPick(null); setSubmit([m.consultantId, pick.id]); }}>
              <Ring score={m.score} size={46} /><div className="mi"><b>{m.consultantName}</b><small>{m.consultantTitle} · {m.visa} · ${m.payRate}/hr</small></div></button>))}
          {best.filter(m => !m.submittedStage).length === 0 && <Empty>Everyone in your view has already been submitted here, or no one is available.</Empty>}
        </Modal>)}
      {submit && <SubmitDialog consultantId={submit[0]} requirementId={submit[1]} onClose={() => setSubmit(null)} onDone={load} />}
    </>
  );
}
