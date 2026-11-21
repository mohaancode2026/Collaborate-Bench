import { Component, ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth';
import Layout from './Layout';
import Landing from './pages/Landing';
import Login from './pages/Login';
import Dashboard from './pages/Dashboard';
import Bench from './pages/Bench';
import Parser from './pages/Parser';
import ResumeBuilder from './pages/ResumeBuilder';
import Jobs from './pages/Jobs';
import Board from './pages/Board';
import ExternalJobs from './pages/ExternalJobs';
import Autopilot from './pages/Autopilot';
import RtrApprove from './pages/RtrApprove';
import VendorMail from './pages/VendorMail';
import MatchPage from './pages/Match';
import Pipeline from './pages/Pipeline';
import Onboarding from './pages/Onboarding';
import Rates from './pages/Rates';
import Team from './pages/Team';
import { Spinner } from './ui';
import { NAV } from './nav';



function RequireAuth({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth();
  const loc = useLocation();
  if (loading) return <div style={{ padding: 60, textAlign: 'center' }}><Spinner dark /> Loading…</div>;
  if (!user) return <Navigate to="/login" replace state={{ from: loc.pathname, reason: 'signin' }} />;
  if (user.mustChangePassword) return <Navigate to="/login" replace state={{ changePassword: true }} />;
  return <>{children}</>;
}

export function Forbidden({ perm }: { perm: string }) {
  const { user } = useAuth();
  return (
    <div className="card"><div className="empty" style={{ padding: '60px 20px' }}>
      <div className="big403">403</div>
      <h2 style={{ color: 'var(--ink)', margin: '6px 0' }}>You don't have access to this page</h2>
      <p>Your role <b>{user?.role}</b> doesn't include the <span className="mono">{perm}</span> permission.</p>
      <p className="mt">Need access? Ask your {user?.role === 'Recruiter' ? 'Team Lead' : 'Delivery Manager'}.</p>
      <a className="btn btn-primary mt2" href="/dashboard">Back to dashboard</a>
    </div></div>
  );
}

function Guard({ perm, children }: { perm: string; children: ReactNode }) {
  const { can } = useAuth();
  return can(perm) ? <ErrorBoundary>{children}</ErrorBoundary> : <Forbidden perm={perm} />;
}

/** Keeps one broken page from blanking the whole app. */
class ErrorBoundary extends Component<{ children: ReactNode }, { error?: Error }> {
  state: { error?: Error } = {};
  static getDerivedStateFromError(error: Error) { return { error }; }
  componentDidCatch(error: Error) { console.error(error); }
  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div className="card"><div className="empty" style={{ padding: 50 }}>
        <h2 style={{ color: 'var(--ink)' }}>This page hit a problem</h2>
        <p className="mt">Something unexpected went wrong while showing this page. Your data is safe.</p>
        <button className="btn btn-primary mt2" onClick={() => location.reload()}>Reload page</button>
      </div></div>
    );
  }
}

const PAGES: Record<string, () => JSX.Element> = {
  '/dashboard': Dashboard, '/bench': Bench, '/parser': Parser, '/resume-builder': ResumeBuilder, '/jobs': Jobs, '/board': Board, '/external-jobs': ExternalJobs, '/autopilot': Autopilot, '/vendor-mail': VendorMail, '/match': MatchPage,
  '/pipeline': Pipeline, '/onboarding': Onboarding, '/rates': Rates, '/team': Team,
};

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Landing />} />
      <Route path="/login" element={<Login />} />
      {/* Public: consultants open their RTR link without an account. */}
      <Route path="/rtr/:token" element={<RtrApprove />} />
      <Route element={<RequireAuth><Layout /></RequireAuth>}>
        {NAV.map(n => {
          const Page = PAGES[n.path];
          return <Route key={n.path} path={n.path} element={<Guard perm={n.perm}><Page /></Guard>} />;
        })}
      </Route>
      <Route path="*" element={<div style={{ padding: 60, textAlign: 'center' }}><h1>Page not found</h1><p className="mt"><a href="/" style={{ color: 'var(--primary)' }}>Go to the home page</a></p></div>} />
    </Routes>
  );
}
