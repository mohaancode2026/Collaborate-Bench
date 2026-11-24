import { FormEvent, useEffect, useRef, useState } from 'react';
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { api, AiText } from './api';
import { NAV } from './nav';
import { useDueReminders } from './followups';
import { useAutopilotCount } from './autopilotCount';
import { useAuth } from './auth';
import { Avatar, useErrorHandler, useToast } from './ui';

/** The Collaborate swirl on its own (small spaces, e.g. the RTR page). */
export const Logo = ({ size = 32 }: { size?: number }) => (
  <img className="logo-mark" src="/brand/collaborate-mark.png" width={size} height={size} alt="" />
);
/** The Collaborate wordmark with a "Bench Sales" tag. <paramref name="light"/>: white lettering for dark backgrounds. */
export const Brand = ({ light, sub = true, height = 34 }: { light?: boolean; sub?: boolean; height?: number }) => (
  <span className={`brand-logo${light ? ' on-dark' : ''}`}>
    <img src={light ? '/brand/collaborate-logo-white.png' : '/brand/collaborate-logo.png'} alt="Collaborate" style={{ height }} />
    {sub && <span className="brand-sub">Bench Sales</span>}
  </span>
);
export const Icon = ({ d, size = 18 }: { d: string; size?: number }) => (
  <svg width={size} height={size} fill="none" stroke="currentColor" strokeWidth="2" viewBox="0 0 24 24"><path d={d} /></svg>
);
export const SPARK = 'M12 3v4M12 17v4M3 12h4M17 12h4M5.6 5.6l2.8 2.8M15.6 15.6l2.8 2.8M5.6 18.4l2.8-2.8M15.6 8.4l2.8-2.8';

export default function Layout() {
  const { user, can, scope, scopeOptions, setScope, logout } = useAuth();
  const [menu, setMenu] = useState(false);
  const [side, setSide] = useState(false);
  const [ai, setAi] = useState<{ claude: boolean } | null>(null);
  const nav = useNavigate();
  const loc = useLocation();
  const notify = useToast();
  const dueFollowUps = useDueReminders();
  const autopilotWaiting = useAutopilotCount();

  useEffect(() => { setSide(false); setMenu(false); }, [loc.pathname]);
  // Keep the current page's link visible when the menu is taller than the screen.
  useEffect(() => { document.querySelector('.side .nav a.on')?.scrollIntoView({ block: 'nearest' }); }, [loc.pathname]);
  useEffect(() => { api<{ claude: boolean }>('/ai/status').then(setAi).catch(() => setAi(null)); }, []);
  if (!user) return null;

  const groups = [...new Set(NAV.map(n => n.group))];
  return (
    <div id="app">
      <div className={`scrim${side ? ' open' : ''}`} onClick={() => setSide(false)} />
      <aside className={`side${side ? ' open' : ''}`}>
        <Link className="brand" to="/" aria-label="Collaborate Bench Sales home"><Brand light height={30} /></Link>
        <nav className="nav">
          {groups.map(g => {
            const items = NAV.filter(n => n.group === g && can(n.perm));
            if (!items.length) return null;
            return (
              <div key={g}>
                <div className="nav-g">{g}</div>
                {items.map(n => <NavLink key={n.path} to={n.path} className={({ isActive }) => (isActive ? 'on' : '')}><Icon d={n.icon} />{n.label}
                  {n.path === '/autopilot' && autopilotWaiting > 0 && <span className="cnt due" title={`${autopilotWaiting} waiting for you in the Autopilot`}>{autopilotWaiting}</span>}
                  {n.path === '/vendor-mail' && dueFollowUps > 0 && <span className="cnt due" title={`${dueFollowUps} vendor follow-up${dueFollowUps === 1 ? '' : 's'} due`}>{dueFollowUps}</span>}</NavLink>)}
              </div>
            );
          })}
        </nav>
        <div className="side-foot">
          <div className={`ai-stat${ai?.claude ? ' live' : ''}`}><i /><span>{ai?.claude ? 'AI: Claude connected' : 'AI: built-in engine'}</span></div>
          <div style={{ opacity: .7, marginTop: 4 }}>{ai?.claude ? 'Agents and the Copilot use Claude, limited to the data your role can see.' : 'Add an Anthropic API key on the server to turn on Claude.'}</div>
        </div>
      </aside>
      <div className="main">
        <header className="topbar">
          <button className="burger" aria-label="Menu" onClick={() => setSide(true)}><Icon d="M3 6h18M3 12h18M3 18h18" /></button>
          <form className="search" onSubmit={(e: FormEvent<HTMLFormElement>) => { e.preventDefault(); const q = new FormData(e.currentTarget).get('q'); nav(`/bench?q=${encodeURIComponent(String(q ?? ''))}`); }}>
            <Icon d="M11 18a7 7 0 100-14 7 7 0 000 14zM21 21l-4.3-4.3" size={16} />
            <input name="q" placeholder="Search consultants, skills, visas…" aria-label="Search consultants" />
          </form>
          <select id="scopeSel" className="inp" value={scope} disabled={scopeOptions.length < 2} aria-label="Data scope"
            onChange={e => { setScope(e.target.value as 'all' | 'team' | 'mine'); notify('info', 'View changed', `Now showing ${e.target.selectedOptions[0].text}.`); }}>
            {scopeOptions.map(([v, t]) => <option key={v} value={v}>{t}</option>)}
          </select>
          <div className="umenu">
            <button id="ubtn" onClick={() => setMenu(m => !m)} aria-haspopup="true" aria-expanded={menu}>
              <Avatar name={user.name} size={32} />
              <span className="un"><b>{user.name}</b><small>{user.role}{user.team ? ` · ${user.team}` : ''}</small></span>
              <Icon d="M6 9l6 6 6-6" size={14} />
            </button>
            <div className={`udrop${menu ? ' open' : ''}`}>
              <div className="ud-h"><Avatar name={user.name} size={40} /><div><b>{user.name}</b><small>{user.email}</small>
                <div><span className="tag v">{user.role}</span>{user.team && <span className="tag s">{user.team}</span>}</div></div></div>
              <div className="ud-i"><span>Reports to</span><b>{user.managerName ?? '—'}</b></div>
              <div className="ud-i"><span>Data you can see</span><b>{user.dataScope === 'OWN' ? 'Your records' : user.dataScope === 'TEAM' ? 'You + your team' : 'Whole organization'}</b></div>
              <Link className="ud-l" to="/login" state={{ changePassword: true, voluntary: true }}>Change password</Link>
              <button className="ud-l" style={{ color: 'var(--bad)' }} onClick={() => logout('manual')}>Sign out</button>
            </div>
          </div>
        </header>
        <main className="content"><Outlet /></main>
      </div>
      {can('ai.copilot') && <Copilot />}
    </div>
  );
}

function Copilot() {
  const { user } = useAuth();
  const [open, setOpen] = useState(false);
  const [msgs, setMsgs] = useState<{ role: 'user' | 'assistant'; content: string }[]>([]);
  const [busy, setBusy] = useState(false);
  const [input, setInput] = useState('');
  const box = useRef<HTMLDivElement>(null);
  const onError = useErrorHandler();
  const notify = useToast();
  useEffect(() => { box.current?.scrollTo(0, box.current.scrollHeight); }, [msgs, busy]);

  const send = async (q: string) => {
    q = q.trim();
    if (!q || busy) return;
    const history = [...msgs, { role: 'user' as const, content: q }];
    setMsgs(history); setInput(''); setBusy(true);
    try {
      const r = await api<AiText>('/ai/copilot', { method: 'POST', body: { messages: history } });
      setMsgs([...history, { role: 'assistant', content: r.text }]);
      if (r.notice) notify('info', 'Built-in engine answered', r.notice);
    } catch (e) { onError(e); setMsgs(history.slice(0, -1)); setInput(q); }
    finally { setBusy(false); }
  };

  return (
    <>
      <button id="cpBtn" onClick={() => setOpen(o => !o)} aria-label="Open AI copilot"><Icon d={SPARK} /><span>Ask AI Copilot</span></button>
      {open && (
        <div id="cpPanel" className="open" role="dialog" aria-label="AI copilot">
          <div className="cp-h"><div className="cpav" style={{ background: 'rgba(255,255,255,.15)' }}>✦</div>
            <b>AI Copilot<small>Answers from data your role can see</small></b>
            <button onClick={() => setMsgs([])}>Clear</button><button onClick={() => setOpen(false)} aria-label="Close">✕</button></div>
          <div id="cpMsgs" ref={box}>
            {msgs.length === 0 ? (
              <div className="cp-empty"><div className="cpav big"><Icon d={SPARK} /></div>
                <b>Hi {user?.name.split(' ')[0]}, I'm your bench sales copilot.</b>
                <p>I only see what your role can see.</p>
                {['Who has no active submission?', 'Which submissions are stuck?', 'Best matches to submit today', 'How is my team doing?'].map(s =>
                  <button key={s} className="cp-sug" onClick={() => send(s)}>{s}</button>)}
              </div>
            ) : msgs.map((m, i) => (
              <div key={i} className={`cpm ${m.role}`}>{m.role === 'assistant' && <div className="cpav"><Icon d={SPARK} size={14} /></div>}<div className="cpb">{m.content}</div></div>
            ))}
            {busy && <div className="cpm assistant"><div className="cpav"><Icon d={SPARK} size={14} /></div><div className="cpb"><span className="spin d" /> Thinking…</div></div>}
          </div>
          <form id="cpForm" onSubmit={e => { e.preventDefault(); send(input); }}>
            <input className="inp" value={input} onChange={e => setInput(e.target.value)} placeholder="Ask about your bench, pipeline, team…" maxLength={1000} />
            <button className="btn btn-primary" type="submit" disabled={busy}>Send</button>
          </form>
        </div>
      )}
    </>
  );
}
