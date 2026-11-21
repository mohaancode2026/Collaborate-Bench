import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { AiText, api, Consultant, Match, Requirement } from '../api';
import { useAuth } from '../auth';
import { SubmitDialog } from '../components';
import { AiBadge, copyText, Empty, Modal, Ring, Spinner, useErrorHandler, useToast } from '../ui';

export default function MatchPage() {
  const { scope, can } = useAuth();
  const [params, setParams] = useSearchParams();
  const onError = useErrorHandler();
  const notify = useToast();
  const mode = params.get('requirement') ? 'j' : (params.get('mode') ?? 'c');
  const [cons, setCons] = useState<Consultant[]>([]);
  const [reqs, setReqs] = useState<Requirement[]>([]);
  const [rows, setRows] = useState<Match[] | null>(null);
  const [submit, setSubmit] = useState<[number, number] | null>(null);
  const [pitch, setPitch] = useState<AiText | 'loading' | null>(null);
  const selected = Number(params.get(mode === 'c' ? 'consultant' : 'requirement')) || (mode === 'c' ? cons[0]?.id : reqs[0]?.id);

  useEffect(() => {
    api<Consultant[]>('/consultants', { query: { scope } }).then(c => setCons(c.filter(x => x.status === 'Bench' || x.status === 'Marketing'))).catch(onError);
    api<Requirement[]>('/requirements', { query: { status: 'Open' } }).then(setReqs).catch(onError);
  }, [scope, onError]);

  const load = () => {
    if (!selected) { setRows([]); return; }
    setRows(null);
    api<Match[]>(mode === 'c' ? `/match/consultant/${selected}` : `/match/requirement/${selected}`, { query: { scope } }).then(setRows).catch(e => { setRows([]); onError(e); });
  };
  useEffect(load, [mode, selected, scope]); // eslint-disable-line react-hooks/exhaustive-deps

  const aiPitch = async () => {
    const top = rows?.find(r => !r.submittedStage);
    if (!top) { notify('warning', 'No match to pitch', 'Pick a consultant or requirement with at least one unsubmitted match.'); return; }
    setPitch('loading');
    try { setPitch(await api<AiText>('/ai/pitch', { method: 'POST', body: { consultantId: top.consultantId, requirementId: top.requirementId } })); }
    catch (e) { setPitch(null); onError(e); }
  };

  return (
    <>
      <div className="ph"><div><h1>Match Agent</h1><p>Scores fit on skills (60%), rate (18%), visa (12%) and location (10%).</p></div>
        <div className="tabs"><button className={mode === 'c' ? 'on' : ''} onClick={() => setParams({ mode: 'c' })}>Consultant → Jobs</button><button className={mode === 'j' ? 'on' : ''} onClick={() => setParams({ mode: 'j' })}>Job → Consultants</button></div></div>
      <div className="card"><div className="card-h">
        <select className="inp" style={{ maxWidth: 420 }} value={selected ?? ''} onChange={e => setParams(mode === 'c' ? { consultant: e.target.value } : { requirement: e.target.value })}>
          {mode === 'c' ? cons.map(c => <option key={c.id} value={c.id}>{c.name} · {c.title}</option>) : reqs.map(r => <option key={r.id} value={r.id}>{r.title} · {r.client}</option>)}
        </select>
        {can('sub.create') && <button className="btn btn-sm btn-outline" onClick={aiPitch}>✦ AI pitch for top match</button>}
      </div><div className="card-b">
        {rows === null ? <Empty><Spinner dark /> Scoring…</Empty> : rows.length === 0 ? <Empty>Nothing to match in your view yet.</Empty> : rows.map(m => (
          <div className="match" key={`${m.consultantId}-${m.requirementId}`}><Ring score={m.score} />
            <div className="mi">
              <b>{mode === 'c' ? `${m.requirementTitle} · ${m.client}` : `${m.consultantName} · ${m.consultantTitle}`}</b>
              <small>{mode === 'c' ? `${m.vendor} · ${m.type} · $${m.billRate}/hr` : `${m.visa} · $${m.payRate}/hr · owner ${m.ownerName}`}</small>
              <div style={{ marginTop: 6 }}>{m.hit.map(s => <span key={s} className="tag g">{s}</span>)}{m.miss.map(s => <span key={s} className="tag r">{s}</span>)}</div>
              <div className="why">{m.why}</div>
            </div>
            <div className="flex" style={{ flexWrap: 'nowrap' }}>
              {can('parser.use') && <Link className="btn btn-sm btn-outline" title="Tailor this consultant's resume to this requirement with AI"
                to={`/resume-builder?consultant=${m.consultantId}&requirement=${m.requirementId}&auto=1`}>✦ Tailor resume</Link>}
              {m.submittedStage ? <span className="tag c">{m.submittedStage}</span> : can('sub.create') && <button className="btn btn-sm btn-primary" onClick={() => setSubmit([m.consultantId, m.requirementId])}>Submit with RTR</button>}
            </div>
          </div>))}
      </div></div>
      {submit && <SubmitDialog consultantId={submit[0]} requirementId={submit[1]} onClose={() => setSubmit(null)} onDone={load} />}
      {pitch && <Modal title="Submission pitch" onClose={() => setPitch(null)}
        footer={<><button className="btn btn-outline" onClick={() => setPitch(null)}>Close</button>{pitch !== 'loading' && <button className="btn btn-primary" onClick={() => copyText(pitch.text, notify)}>Copy pitch</button>}</>}>
        {pitch === 'loading' ? <Empty><Spinner dark /> Writing…</Empty> : <><AiBadge source={pitch.source} /><div className="jd mt">{pitch.text}</div></>}
      </Modal>}
    </>
  );
}
