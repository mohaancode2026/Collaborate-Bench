import { DragEvent, useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth';
import { Brand } from '../Layout';

// Marketing page. Everything listed here exists in the product; no invented customers, numbers or testimonials.

const PILLARS = [
  {
    tag: 'Bench intake', title: 'AI resume parsing',
    text: 'Drop a PDF, DOCX or TXT resume and get a ready-to-market consultant profile in seconds.',
    points: ['Name, contact, title, visa, rate, location and years of experience extracted', 'Duplicate check before the consultant is added', 'Instant fit scores against every open requirement', 'Tailored resume versions per client, without invented experience'],
  },
  {
    tag: 'Delivery', title: 'Drag-and-drop pipeline',
    text: 'See every submission on one board and move it from Submitted to Billing with a drag.',
    points: ['Six stages, Submitted → Vendor screen → Interview → Offer → MSA / PO → Billing', 'RTR required and duplicate submissions blocked', 'Stuck-for-3-days flags so nothing goes quiet', 'Onboarding checklist must be done before Billing'],
  },
  {
    tag: 'Vendors', title: 'Vendor submission tracker',
    text: 'Know who you sent, to which vendor, at what rate, and who still owes you a reply.',
    points: ['Outlook emails with vendors tracked automatically', 'Send submissions and follow-ups from your own mailbox', 'Reminders when a vendor hasn\'t replied in 48 hours', 'Recruiter-wise report with Excel export'],
  },
];

const STEPS = [
  ['Build the bench', 'Parse resumes into clean profiles and hotlists your team can market the same day.'],
  ['Find the right jobs', 'Match consultants to requirements, or search LinkedIn, Indeed and more by skill set.'],
  ['Submit with an RTR', 'Tailor the resume, confirm the rate and RTR, and email the vendor from the portal.'],
  ['Chase every reply', 'Track vendor emails and get a reminder when a thread goes quiet.'],
  ['Win the interview', 'AI interview prep, stage moves on the board, and offers tracked to signature.'],
  ['Onboard and bill', 'MSA, PO, background check and first invoice on one checklist, then the consultant bills.'],
];

const MORE = [
  ['Explainable match agent', 'Every match is scored on skills, rate, visa and location, with the reason in plain words.'],
  ['External job search', 'Jobs from LinkedIn, Indeed, Glassdoor and company sites, matched to each consultant\'s skills.'],
  ['AI resume builder', 'Rewords and reorders the real resume for each client. Gaps are listed, never added.'],
  ['Email templates', 'Team and personal templates with fields like {{consultant.name}}, filled in for you.'],
  ['Requirement sourcing', 'Paste a vendor email and get the client, rate, visa and C2C/W2 terms as a requirement.'],
  ['Recruiter reports', 'Submissions, interviews and placements by recruiter and date range, exported to Excel.'],
  ['Team hierarchy', 'Delivery Manager, Team Lead and Recruiter, each seeing exactly their own data.'],
  ['AI copilot', 'Ask "who on my bench fits this job?" and get an answer from your own data.'],
];

const BEFORE = ['Hotlists in spreadsheets that are out of date by lunch', 'Submissions buried in personal inboxes', 'No idea which vendor still owes a reply', 'The same consultant sent twice to one requirement', 'Managers chasing recruiters for status updates'];
const AFTER = ['One live bench with fit scores for every open job', 'Every vendor email tracked against the consultant', 'Automatic follow-up reminders after 48 hours', 'Duplicates blocked and RTR required on every submission', 'A pipeline and recruiter report that are always current'];

const FAQ = [
  ['Who is Collaborate-Bench for?', 'US IT staffing and bench sales teams that market consultants on C2C, W2 or 1099 to vendors and end clients.'],
  ['Do I need an AI subscription?', 'No. Every AI feature has a built-in engine that works without one. Adding an Anthropic API key gives better parsing, writing and tailoring.'],
  ['Does it work with Outlook?', 'Yes. Sign in with your Microsoft 365 account and emails with your listed vendors are tracked. You can also send from the portal using your own mailbox.'],
  ['Can recruiters see each other\'s consultants?', 'Only if their role allows it. Recruiters see their own records, Team Leads see their team, and managers see everyone.'],
  ['Can it apply on LinkedIn for me?', 'LinkedIn doesn\'t allow outside tools to apply. Collaborate-Bench finds the jobs, prepares the tailored resume and cover note, and opens the application for you.'],
  ['Where is our data stored?', 'In your own SQL Server database. Job searches send only skills, title and location, never consultant names or contact details.'],
];

type Card = { id: number; name: string; role: string; vendor: string };
const COLS = ['Submitted', 'Vendor screen', 'Interview', 'Offer'];
const START: Record<string, Card[]> = {
  Submitted: [{ id: 1, name: 'Ravi K.', role: 'Senior Java Developer', vendor: 'Prime vendor · $78/hr' }, { id: 2, name: 'Lakshmi R.', role: 'Data Engineer', vendor: 'Direct client · $82/hr' }],
  'Vendor screen': [{ id: 3, name: 'Anil G.', role: 'DevOps Engineer', vendor: 'Tier-1 · $85/hr' }],
  Interview: [{ id: 4, name: 'Sneha P.', role: 'Java Full Stack', vendor: 'Implementation partner · $74/hr' }],
  Offer: [],
};

export default function Landing() {
  const { user } = useAuth();
  const [faq, setFaq] = useState<number | null>(0);
  const app = user ? '/dashboard' : '/login';

  return (
    <div id="landing">
      <nav className="lnav">
        <Link className="brand" to="/" aria-label="Collaborate Bench Sales home"><Brand light /></Link>
        <div className="lnav-links"><a href="#platform">Platform</a><a href="#workflow">Workflow</a><a href="#features">Features</a><a href="#security">Security</a><a href="#faq">FAQ</a></div>
        <div className="flex">{!user && <Link className="btn btn-ghost-l btn-sm" to="/login">Sign in</Link>}<Link className="btn btn-light btn-sm" to="/dashboard">Open workspace →</Link></div>
      </nav>

      <section className="hero">
        <div>
          <span className="pill"><span className="dot" />Built for US IT bench sales · C2C · W2 · 1099</span>
          <h1>Market your bench and <span>place consultants faster</span>, with every submission tracked.</h1>
          <p className="lead">Collaborate-Bench brings resume parsing, job matching, vendor submissions, interviews and onboarding into one workspace your whole desk shares, with AI doing the busywork in between.</p>
          <div className="hero-cta">
            <Link className="btn btn-light" to={app}>{user ? 'Open your workspace →' : 'Sign in to get started →'}</Link>
            <a className="btn btn-ghost-l" href="#workflow">See how it works</a>
          </div>
          <p className="lp-trust">Runs on your own SQL Server · Microsoft 365 sign-in · Role-based access for every recruiter</p>
        </div>
        <PipelineDemo />
      </section>

      <div className="lp-strip">
        {[['Resume → billing', 'one shared workspace'], ['RTR on every submission', 'duplicates blocked'], ['Every vendor email', 'tracked and followed up'], ['Team-level security', 'each role sees its own data']].map(([b, s]) => (
          <div key={b}><b>{b}</b><small>{s}</small></div>))}
      </div>

      <section className="section" id="platform">
        <div className="eyebrow">Platform</div>
        <h2>The three things a bench sales desk runs on</h2>
        <p className="sub">Source consultants, keep the pipeline honest, and stay on top of every vendor, without switching between spreadsheets, inboxes and job boards.</p>
        <div className="lp-pillars">
          {PILLARS.map(p => (
            <div className="lp-pillar" key={p.title}>
              <span className="lp-tag">{p.tag}</span>
              <h3>{p.title}</h3>
              <p>{p.text}</p>
              <ul>{p.points.map(x => <li key={x}>{x}</li>)}</ul>
            </div>))}
        </div>
      </section>

      <section className="section alt" id="workflow">
        <div className="eyebrow">Workflow</div>
        <h2>One flow, from resume to first invoice</h2>
        <p className="sub">Each step is visible to the people who need it, and nothing waits in someone's inbox.</p>
        <div className="lp-steps">
          {STEPS.map(([t, d], i) => (
            <div className="lp-step" key={t}>
              <span className="lp-num">{String(i + 1).padStart(2, '0')}</span>
              <h4>{t}</h4>
              <p>{d}</p>
            </div>))}
        </div>
      </section>

      <section className="section" id="features">
        <div className="eyebrow">Features</div>
        <h2>Everything else your team asks for</h2>
        <div className="fgrid lp-more">
          {MORE.map(([t, d]) => (
            <div className="fcard" key={t}>
              <div className="ficon"><svg width="22" height="22" fill="none" stroke="#fff" strokeWidth="2" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 2l3 7h7l-5.5 4.5 2 7.5-6.5-4.5L5.5 21l2-7.5L2 9h7z" /></svg></div>
              <h3>{t}</h3><p>{d}</p>
            </div>))}
        </div>
      </section>

      <section className="section alt">
        <div className="eyebrow">Why switch</div>
        <h2>From scattered to in control</h2>
        <div className="lp-compare">
          <div className="lp-col before"><h3>Spreadsheets and inboxes</h3><ul>{BEFORE.map(x => <li key={x}>{x}</li>)}</ul></div>
          <div className="lp-col after"><h3>With Collaborate-Bench</h3><ul>{AFTER.map(x => <li key={x}>{x}</li>)}</ul></div>
        </div>
      </section>

      <section className="section" id="security">
        <div className="eyebrow">Security & trust</div>
        <h2>Built for consultant data you're responsible for</h2>
        <div className="lp-sec">
          {[['Team-level visibility', 'Every query is limited to the records your role may see.'],
            ['Full audit trail', 'Sign-ins, submissions, exports, downloads and emails are logged.'],
            ['Your database', 'Data stays in your own SQL Server, not a shared cloud.'],
            ['Honest resumes', 'The AI never adds skills or experience a consultant doesn\'t have.'],
            ['Private job searches', 'Outside job APIs only see skills, title and location.'],
            ['Microsoft sign-in', 'Company accounts sign in with Microsoft 365. Others use a password.']].map(([t, d]) => (
            <div key={t}><b>{t}</b><p>{d}</p></div>))}
        </div>
      </section>

      <section className="section alt" id="faq">
        <div className="eyebrow">FAQ</div>
        <h2>Questions teams ask before they start</h2>
        <div className="lp-faq">
          {FAQ.map(([q, a], i) => (
            <div key={q} className={`lp-q${faq === i ? ' open' : ''}`}>
              <button aria-expanded={faq === i} onClick={() => setFaq(faq === i ? null : i)}>{q}<span aria-hidden="true">{faq === i ? '−' : '+'}</span></button>
              {faq === i && <p>{a}</p>}
            </div>))}
        </div>
      </section>

      <section className="section">
        <div className="cta-band">
          <div><h2>Put your bench to work today.</h2><p style={{ opacity: .8, marginTop: 6 }}>Sign in with your work account, or ask your admin for access.</p></div>
          <Link className="btn btn-light" to={app}>{user ? 'Open your workspace →' : 'Sign in →'}</Link>
        </div>
      </section>

      <footer className="lfoot lp-foot">
        <div><div className="brand"><Brand light /></div><p>Bench sales software for US IT staffing teams.</p></div>
        <div><b>Product</b><a href="#platform">Platform</a><a href="#workflow">Workflow</a><a href="#features">Features</a></div>
        <div><b>Trust</b><a href="#security">Security</a><a href="#faq">FAQ</a></div>
        <div><b>Workspace</b><Link to="/login">Sign in</Link><Link to="/dashboard">Open workspace</Link></div>
        <small>© {new Date().getFullYear()} Collaborate‑Bench Sales · US IT staffing · C2C · W2 · 1099</small>
      </footer>
    </div>
  );
}

/** A small, working drag-and-drop board in the hero, so visitors can feel the pipeline. Sample data only. */
function PipelineDemo() {
  const [cols, setCols] = useState(START);
  const [over, setOver] = useState<string | null>(null);
  const drop = (e: DragEvent, to: string) => {
    e.preventDefault(); setOver(null);
    const id = Number(e.dataTransfer.getData('text/plain'));
    setCols(c => {
      const from = Object.keys(c).find(k => c[k].some(x => x.id === id));
      if (!from || from === to) return c;
      const card = c[from].find(x => x.id === id)!;
      return { ...c, [from]: c[from].filter(x => x.id !== id), [to]: [...c[to], card] };
    });
  };
  return (
    <div className="mock lp-board" aria-label="Pipeline preview with sample data">
      <div className="flex between"><b style={{ fontSize: 14 }}>Submissions pipeline</b><span className="lp-hint">Try dragging a card →</span></div>
      <div className="lp-cols">
        {COLS.map(col => (
          <div key={col} className={`lp-colm${over === col ? ' over' : ''}`} onDragOver={e => { e.preventDefault(); setOver(col); }} onDragLeave={() => setOver(null)} onDrop={e => drop(e, col)}>
            <div className="lp-colh">{col}<span>{cols[col].length}</span></div>
            {cols[col].map(c => (
              <div key={c.id} className="lp-card" draggable onDragStart={e => e.dataTransfer.setData('text/plain', String(c.id))}>
                <b>{c.name}</b><small>{c.role}</small><small className="v">{c.vendor}</small>
              </div>))}
          </div>))}
      </div>
      <small className="lp-sample">Sample data</small>
    </div>
  );
}
