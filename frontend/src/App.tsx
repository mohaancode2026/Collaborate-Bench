import { Component, lazy, ReactNode, Suspense } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth';
import { roleLabel } from './roles';
import Layout from './Layout';
import Login from './pages/Login';
import { Spinner } from './ui';
import { NAV } from './nav';

// Each page is its own file, downloaded the first time it's opened, so the app starts faster.
const Dashboard = lazy(() => import('./pages/Dashboard'));
const Bench = lazy(() => import('./pages/Bench'));
const ConsultantProfilePage = lazy(() => import('./pages/ConsultantProfile'));
const Parser = lazy(() => import('./pages/Parser'));
const ResumeBuilder = lazy(() => import('./pages/ResumeBuilder'));
const Jobs = lazy(() => import('./pages/Jobs'));
const Board = lazy(() => import('./pages/Board'));
const ExternalJobs = lazy(() => import('./pages/ExternalJobs'));
const Autopilot = lazy(() => import('./pages/Autopilot'));
const RtrApprove = lazy(() => import('./pages/RtrApprove'));
const VendorMail = lazy(() => import('./pages/VendorMail'));
const Inbox = lazy(() => import('./pages/Inbox'));
const MatchPage = lazy(() => import('./pages/Match'));
const Pipeline = lazy(() => import('./pages/Pipeline'));
const LinkedInLeads = lazy(() => import('./pages/LinkedInLeads'));
const Onboarding = lazy(() => import('./pages/Onboarding'));
const Rates = lazy(() => import('./pages/Rates'));
const Team = lazy(() => import('./pages/Team'));
const Landing = lazy(() => import('./pages/Landing'));

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
      <p>Your role <b>{roleLabel(user?.role)}</b> doesn't include the <span className="mono">{perm}</span> permission.</p>
      <p className="mt">Need access? Ask your {user?.role === 'Recruiter' ? 'Team Lead' : 'Director of Sales'}.</p>
      <a className="btn btn-primary mt2" href="/dashboard">Back to dashboard</a>
    </div></div>
  );
}

function Guard({ perm, children }: { perm: string; children: ReactNode }) {
  const { can } = useAuth();
  return can(perm) ? <ErrorBoundary><Suspense fallback={<PageLoading />}>{children}</Suspense></ErrorBoundary> : <Forbidden perm={perm} />;
}

const PageLoading = () => <div style={{ padding: 60, textAlign: 'center' }}><Spinner dark /> Loading…</div>;

/** Keeps one broken page from blanking the whole app (also a page file that failed to download after an update: Reload fixes it). */
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

const PAGES: Record<string, React.ComponentType> = {
  '/dashboard': Dashboard, '/bench': Bench, '/parser': Parser, '/resume-builder': ResumeBuilder, '/jobs': Jobs, '/board': Board, '/external-jobs': ExternalJobs, '/autopilot': Autopilot, '/vendor-mail': VendorMail, '/match': MatchPage,
  '/inbox': Inbox, '/linkedin': LinkedInLeads, '/pipeline': Pipeline, '/onboarding': Onboarding, '/rates': Rates, '/team': Team,
};

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Suspense fallback={<PageLoading />}><Landing /></Suspense>} />
      <Route path="/login" element={<Login />} />
      {/* Public: consultants open their RTR link without an account. */}
      <Route path="/rtr/:token" element={<Suspense fallback={<PageLoading />}><RtrApprove /></Suspense>} />
      <Route element={<RequireAuth><Layout /></RequireAuth>}>
        {NAV.map(n => {
          const Page = PAGES[n.path];
          return <Route key={n.path} path={n.path} element={<Guard perm={n.perm}><Page /></Guard>} />;
        })}
        <Route path="/bench/:id" element={<Guard perm="bench.view"><ConsultantProfilePage /></Guard>} />
      </Route>
      <Route path="*" element={<div style={{ padding: 60, textAlign: 'center' }}><h1>Page not found</h1><p className="mt"><a href="/" style={{ color: 'var(--primary)' }}>Go to the home page</a></p></div>} />
    </Routes>
  );
}
