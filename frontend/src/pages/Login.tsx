import { FormEvent, useEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { api, ApiError, User } from '../api';
import { useAuth } from '../auth';
import { Logo } from '../Layout';
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
        <div className="login-art">
          <Link className="brand" to="/"><Logo />Collaborate‑Bench</Link>
          <div>
            <h2 style={{ fontSize: 34, lineHeight: 1.1, fontWeight: 800, letterSpacing: -1 }}>One desk. Clear roles.<br />AI on every step.</h2>
            <p style={{ opacity: .8, marginTop: 12, maxWidth: 440 }}>Delivery Managers see the whole organization, Team Leads see their own and their team's data, and Recruiters see their own pipeline. SQL Server enforces it on every query.</p>
            <div className="hier">
              <div className="hb">Delivery Manager<small>All teams · full access</small></div>
              <div className="hrow"><div className="hb">Team Lead · Alpha<small>Own + team data</small></div><div className="hb">Team Lead · Beta<small>Own + team data</small></div></div>
              <div className="hrow">{[1, 2, 3, 4].map(i => <div key={i} className="hb s">Recruiter</div>)}</div>
            </div>
          </div>
          <small style={{ opacity: .6 }}>© {new Date().getFullYear()} Collaborate‑Bench Sales</small>
        </div>
        <div className="login-card">
          {changing ? <ChangePassword voluntary={!!loc.state?.voluntary} onDone={async () => { await refresh(); nav('/dashboard', { replace: true }); }} /> : (
            <>
              <h1>Sign in to your workspace</h1>
              <p className="muted" style={{ margin: '6px 0 20px' }}>{microsoft ? 'Use your company Microsoft account, or any email with a password.' : 'Use your work email and password.'}</p>
              {loc.state?.reason === 'signin' && !alert && <Alert type="info" title="Please sign in">Sign in to open the workspace.</Alert>}
              {alert && <Alert type={alert.type} title={alert.title}>{alert.detail}</Alert>}
              {microsoft && <>
                <button type="button" className="btn btn-outline ms-btn" onClick={signInWithMicrosoft} disabled={msBusy}>
                  {msBusy ? <Spinner dark /> : <MicrosoftLogo />} Sign in with Microsoft (Outlook)</button>
                <p className="muted" style={{ fontSize: 12.5, margin: '6px 0 0', textAlign: 'center' }}>Company accounts. Your emails with vendors are tracked automatically.</p>
                <div className="or"><span>or sign in with email and password</span></div>
              </>}
              <form onSubmit={submit} noValidate>
                <Field label="Work email" error={errors.email}><input className="inp" type="email" autoComplete="username" value={email} onChange={e => setEmail(e.target.value)} placeholder="you@company.com" autoFocus /></Field>
                <Field label={<span className="flex between" style={{ width: '100%' }}>Password<a href="#" style={{ fontSize: 12.5, color: 'var(--primary)', fontWeight: 600 }}
                  onClick={e => { e.preventDefault(); setAlert({ type: 'info', title: 'Forgot your password?', detail: 'Ask your Delivery Manager or admin to reset it from Team & Access. You will get a temporary password.' }); }}>Forgot password?</a></span>} error={errors.password}>
                  <div className="pw"><input className="inp" type={show ? 'text' : 'password'} autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} placeholder="••••••••" />
                    <button type="button" onClick={() => setShow(s => !s)}>{show ? 'Hide' : 'Show'}</button></div>
                </Field>
                <button className="btn btn-dark" type="submit" disabled={busy} style={{ width: '100%', justifyContent: 'center', padding: 12 }}>{busy ? <Spinner /> : 'Sign in'}</button>
              </form>
              {import.meta.env.DEV && (
                <div className="demo"><b style={{ fontSize: 13 }}>Demo accounts</b> <span className="muted" style={{ fontSize: 12.5 }}>· password <span className="mono">Demo@123</span> · click to fill</span>
                  <div className="demo-grid">{DEMO.map(([n, em, r]) => (
                    <button key={em} type="button" className="demo-acc" onClick={() => { setEmail(em); setPassword('Demo@123'); setErrors({}); setAlert(null); }}>
                      <Avatar name={n} size={26} /><span><b>{n}</b><small>{r}</small></span></button>))}</div>
                </div>
              )}
              <Link to="/" className="muted" style={{ fontSize: 13, marginTop: 18 }}>← Back to website</Link>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

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
