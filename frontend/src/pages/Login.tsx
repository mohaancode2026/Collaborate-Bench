import { FormEvent, useEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { api, ApiError, User } from '../api';
import { useAuth } from '../auth';
import { Brand, Icon } from '../Layout';
import { Alert, Avatar, Field, FieldErrors, Spinner, useToast } from '../ui';

const DEMO = [
  ['System Admin', 'admin@collabbench.io', 'Admin'], ['Priya Sharma', 'priya@collabbench.io', 'Delivery Manager'],
  ['Arjun Reddy', 'arjun@collabbench.io', 'Team Lead · Alpha'], ['Kavya Nair', 'kavya@collabbench.io', 'Team Lead · Beta'],
  ['Megha Iyer', 'megha@collabbench.io', 'Recruiter · Alpha'], ['Sanjay Das', 'sanjay@collabbench.io', 'Recruiter · Beta'],
  ['Vivek Rao', 'vivek@collabbench.io', 'Recruiter · deactivated'],
];

export default function Login() {
  const { user, login, completeLogin, refresh } = useAuth();
  const loc = useLocation() as { state?: { from?: string; changePassword?: boolean; voluntary?: boolean; reason?: string } };
  const nav = useNavigate();
  const notify = useToast();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [show, setShow] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [alert, setAlert] = useState<{ type: 'error' | 'warning' | 'info'; title: string; detail: string } | null>(null);
  const changing = !!user && (user.mustChangePassword || loc.state?.changePassword);
  const [microsoft, setMicrosoft] = useState(false);
  const [msBusy, setMsBusy] = useState(false);
  const handled = useRef(false);

  useEffect(() => { api<{ microsoft: boolean }>('/auth/providers').then(p => setMicrosoft(p.microsoft)).catch(() => {}); }, []);

  // Back from Microsoft: ?ms=<one-time code> on success, ?msError=<reason> otherwise.
  useEffect(() => {
    if (handled.current) return;
    const q = new URLSearchParams(window.location.search);
    const code = q.get('ms'), err = q.get('msError');
    if (!code && !err) return;
    handled.current = true;
    window.history.replaceState(null, '', '/login');
    if (err) { setAlert(MS_ERRORS[err] ?? MS_ERRORS.failed); return; }
    setMsBusy(true);
    api<{ token: string; user: User }>('/auth/microsoft/exchange', { method: 'POST', body: { code } })
      .then(res => {
        const u = completeLogin(res);
        notify('success', `Welcome back, ${u.name.split(' ')[0]}`, 'Signed in with Microsoft. Your vendor emails are being tracked.');
        nav('/dashboard', { replace: true });
      })
      .catch(e => setAlert(e instanceof ApiError ? { type: 'error', title: e.title, detail: e.detail } : MS_ERRORS.failed))
      .finally(() => setMsBusy(false));
  }, [completeLogin, nav, notify]);

  const signInWithMicrosoft = async () => {
    setMsBusy(true); setAlert(null);
    try {
      const r = await api<{ url: string }>('/auth/microsoft/start', { method: 'POST', body: { mode: 'login', loginHint: /@/.test(email) ? email.trim() : undefined } });
      window.location.assign(r.url);
    } catch (e) {
      setMsBusy(false);
      setAlert(e instanceof ApiError ? { type: 'error', title: e.title, detail: e.detail } : MS_ERRORS.failed);
    }
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setErrors({}); setAlert(null);
    const local: FieldErrors = {};
    if (!email.trim()) local.email = ['Enter your work email'];
    if (!password) local.password = ['Enter your password'];
    if (Object.keys(local).length) { setErrors(local); setAlert({ type: 'warning', title: 'Missing information', detail: 'Fill in both fields to sign in.' }); return; }
    setBusy(true);
    try {
      const u = await login(email.trim(), password);
      setPassword('');
      if (u.mustChangePassword) { notify('info', 'Choose a new password', 'Your admin gave you a temporary password. Set your own to continue.'); return; }
      notify('success', `Welcome back, ${u.name.split(' ')[0]}`, `Signed in as ${u.role}${u.team ? ' · ' + u.team : ''}.`);
      nav(loc.state?.from ?? '/dashboard', { replace: true });
    } catch (err) {
      if (err instanceof ApiError) {
        if (err.errors) setErrors(err.errors);
        if (err.code === 'INVALID_CREDENTIALS') setErrors({ password: ['Incorrect email or password'] });
        setAlert({ type: err.code === 'VALIDATION' || err.code === 'RATE_LIMITED' ? 'warning' : 'error', title: err.title, detail: err.detail });
      } else setAlert({ type: 'error', title: 'Something went wrong', detail: 'Please try again.' });
    } finally { setBusy(false); }
  };

  return (
    <div id="loginScreen">
      <div className="login-wrap">
        <aside className="login-art">
          <Link to="/" className="login-brand" aria-label="Collaborate Bench Sales home"><Brand light height={40} /></Link>
          <div className="login-pitch">
            <span className="login-kicker">US IT bench sales, end to end</span>
            <h2>Place your bench faster,<br />with AI on every step.</h2>
            <p>Parse resumes, match consultants to open requirements, submit to vendors and follow up, all from one workspace your whole desk shares.</p>
            <ul className="login-points">
              {POINTS.map(([icon, title, text]) => (
                <li key={title}><span className="lp-ico"><Icon d={icon} size={18} /></span><span><b>{title}</b>{text}</span></li>
              ))}
            </ul>
          </div>
          <div className="login-foot">
            <span><Icon d="M12 2l8 4v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6z" size={14} /> Role-based access: recruiters see their own pipeline, leads see their team.</span>
            <small>© {new Date().getFullYear()} Collaborate · Bench Sales</small>
          </div>
        </aside>

        <main className="login-side">
          <div className="login-card">
            <Link to="/" className="login-card-logo" aria-label="Collaborate Bench Sales home"><Brand height={44} /></Link>
            {changing ? <ChangePassword voluntary={!!loc.state?.voluntary} onDone={async () => { await refresh(); nav('/dashboard', { replace: true }); }} /> : (
              <>
                <h1>Welcome back</h1>
                <p className="login-sub">{microsoft ? 'Sign in with your company Microsoft account, or with your email and password.' : 'Sign in with your work email and password.'}</p>
                {loc.state?.reason === 'signin' && !alert && <Alert type="info" title="Please sign in">Sign in to open the workspace.</Alert>}
                {alert && <Alert type={alert.type} title={alert.title}>{alert.detail}</Alert>}
                {microsoft && <>
                  <button type="button" className="btn btn-outline ms-btn" onClick={signInWithMicrosoft} disabled={msBusy}>
                    {msBusy ? <Spinner dark /> : <MicrosoftLogo />} Continue with Microsoft</button>
                  <p className="login-hint">Company accounts. Your emails with vendors are tracked automatically.</p>
                  <div className="or"><span>or with email</span></div>
                </>}
                <form onSubmit={submit} noValidate>
                  <Field label="Work email" error={errors.email}><input className="inp" type="email" autoComplete="username" value={email} onChange={e => setEmail(e.target.value)} placeholder="you@company.com" autoFocus /></Field>
                  <Field label={<span className="flex between" style={{ width: '100%' }}>Password<a href="#" className="login-link"
                    onClick={e => { e.preventDefault(); setAlert({ type: 'info', title: 'Forgot your password?', detail: 'Ask your Delivery Manager or admin to reset it from Team & Access. You will get a temporary password.' }); }}>Forgot password?</a></span>} error={errors.password}>
                    <div className="pw"><input className="inp" type={show ? 'text' : 'password'} autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} placeholder="Your password" />
                      <button type="button" onClick={() => setShow(s => !s)} aria-label={show ? 'Hide password' : 'Show password'}>{show ? 'Hide' : 'Show'}</button></div>
                  </Field>
                  <button className="btn btn-primary login-submit" type="submit" disabled={busy}>{busy ? <Spinner /> : 'Sign in'}</button>
                </form>
                {import.meta.env.DEV && (
                  <details className="demo">
                    <summary><b>Demo accounts</b> <span className="muted">· password <span className="mono">Demo@123</span> · click one to fill in</span></summary>
                    <div className="demo-grid">{DEMO.map(([n, em, r]) => (
                      <button key={em} type="button" className="demo-acc" onClick={() => { setEmail(em); setPassword('Demo@123'); setErrors({}); setAlert(null); }}>
                        <Avatar name={n} size={26} /><span><b>{n}</b><small>{r}</small></span></button>))}</div>
                  </details>
                )}
                <Link to="/" className="login-back">← Back to website</Link>
              </>
            )}
          </div>
        </main>
      </div>
    </div>
  );
}

/** What the left panel highlights: [icon path, title, one line]. */
const POINTS: [string, string, string][] = [
  ['M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8zM14 2v6h6', 'AI resume parsing', 'Turn any resume or LinkedIn profile into a ready bench profile.'],
  ['M12 2l3 7h7l-5.5 4.5 2 7.5-6.5-4.5L5.5 21l2-7.5L2 9h7z', 'Explainable matching', 'See which consultant fits which requirement, and why.'],
  ['M4 4h16a2 2 0 012 2v12a2 2 0 01-2 2H4a2 2 0 01-2-2V6a2 2 0 012-2zM22 6l-10 7L2 6', 'Vendor submissions & follow-ups', 'RTR, submissions and reminders tracked through to billing.'],
  ['M13 2L3 14h9l-1 8 10-12h-9l1-8z', 'Autopilot', 'Prepares matches and follow-ups for you to approve.'],
];

const MS_ERRORS: Record<string, { type: 'error' | 'warning' | 'info'; title: string; detail: string }> = {
  not_registered: { type: 'error', title: 'Account not set up', detail: "Your Microsoft account isn't registered in Collaborate-Bench. Ask your admin to add your work email in Team & Access." },
  deactivated: { type: 'error', title: 'Account deactivated', detail: 'Your account has been deactivated. Contact your admin if you think this is a mistake.' },
  locked: { type: 'error', title: 'Account temporarily locked', detail: 'Too many failed password attempts. Try again in a few minutes, or ask your admin to unlock it.' },
  cancelled: { type: 'info', title: 'Microsoft sign-in cancelled', detail: 'You can try again, or sign in with your email and password.' },
  expired: { type: 'warning', title: 'Sign-in took too long', detail: 'The Microsoft sign-in expired. Please try again.' },
  failed: { type: 'error', title: "Couldn't sign in with Microsoft", detail: 'Microsoft sign-in failed. Try again, or sign in with your email and password.' },
};

const MicrosoftLogo = () => (
  <svg width="18" height="18" viewBox="0 0 21 21" aria-hidden="true"><path fill="#f25022" d="M0 0h10v10H0z" /><path fill="#7fba00" d="M11 0h10v10H11z" /><path fill="#00a4ef" d="M0 11h10v10H0z" /><path fill="#ffb900" d="M11 11h10v10H11z" /></svg>
);

function ChangePassword({ onDone, voluntary }: { onDone: () => void; voluntary: boolean }) {
  const { logout } = useAuth();
  const notify = useToast();
  const [cur, setCur] = useState(''); const [next, setNext] = useState(''); const [confirm, setConfirm] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [alert, setAlert] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const rules = [[next.length >= 10, '10+ characters'], [/[A-Z]/.test(next), 'Uppercase'], [/[a-z]/.test(next), 'Lowercase'], [/\d/.test(next), 'Number'], [/[^A-Za-z0-9]/.test(next), 'Symbol']] as const;

  const submit = async (e: FormEvent) => {
    e.preventDefault(); setErrors({}); setAlert(null);
    if (next !== confirm) { setErrors({ confirm: ['Passwords don\'t match'] }); return; }
    setBusy(true);
    try {
      await api('/auth/change-password', { method: 'POST', body: { currentPassword: cur, newPassword: next } });
      notify('success', 'Password updated', 'Use your new password next time you sign in.');
      onDone();
    } catch (err) {
      if (err instanceof ApiError) { if (err.errors) setErrors(err.errors); setAlert(err.detail); }
    } finally { setBusy(false); }
  };
  return (
    <>
      <h1>{voluntary ? 'Change your password' : 'Set a new password'}</h1>
      <p className="muted" style={{ margin: '6px 0 20px' }}>{voluntary ? 'Choose a strong password you don\'t use anywhere else.' : 'You signed in with a temporary password. Choose your own to continue.'}</p>
      {alert && <Alert type="error" title="Couldn't change password">{alert}</Alert>}
      <form onSubmit={submit} noValidate>
        <Field label="Current (temporary) password" error={errors.currentPassword}><input className="inp" type="password" value={cur} onChange={e => setCur(e.target.value)} autoComplete="current-password" /></Field>
        <Field label="New password" error={errors.newPassword}><input className="inp" type="password" value={next} onChange={e => setNext(e.target.value)} autoComplete="new-password" /></Field>
        <div className="chips" style={{ margin: '-6px 0 14px' }}>{rules.map(([ok, t]) => <span key={t} className={`tag ${ok ? 'g' : 's'}`}>{ok ? '✓' : '·'} {t}</span>)}</div>
        <Field label="Confirm new password" error={errors.confirm}><input className="inp" type="password" value={confirm} onChange={e => setConfirm(e.target.value)} autoComplete="new-password" /></Field>
        <button className="btn btn-dark" type="submit" disabled={busy} style={{ width: '100%', justifyContent: 'center', padding: 12 }}>{busy ? <Spinner /> : 'Save password'}</button>
      </form>
      <button className="btn btn-sm mt" style={{ color: 'var(--muted)' }} onClick={() => (voluntary ? history.back() : logout('manual'))}>{voluntary ? '← Cancel' : 'Sign out'}</button>
    </>
  );
}
