import { createContext, ReactNode, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, ApiError, tokenStore, User } from './api';
import { Alert, Modal, useToast } from './ui';

type Scope = 'all' | 'team' | 'mine';
interface AuthState {
  user: User | null;
  loading: boolean;
  scope: Scope;
  scopeOptions: [Scope, string][];
  setScope: (s: Scope) => void;
  can: (perm: string) => boolean;
  login: (email: string, password: string) => Promise<User>;
  /** Finish a sign-in that the server already completed (Microsoft). */
  completeLogin: (res: { token: string; user: User }) => User;
  logout: (reason?: 'idle' | 'expired' | 'manual') => void;
  refresh: () => Promise<void>;
}
const Ctx = createContext<AuthState>(null as unknown as AuthState);
export const useAuth = () => useContext(Ctx);

const IDLE_WARN_MS = 25 * 60_000;
const IDLE_OUT_MS = 30 * 60_000;

export function scopeOptionsFor(u: User | null): [Scope, string][] {
  if (!u) return [['mine', 'My records']];
  if (u.role === 'Admin') return [['all', 'All data']];
  if (u.role === 'Delivery Manager') return [['all', 'All teams'], ['team', 'My org'], ['mine', 'My records']];
  if (u.role === 'Manager') return [['all', 'Whole bench'], ['team', 'My org'], ['mine', 'My records']];
  if (u.role === 'Team Lead') return [['team', `My team${u.team ? ' · ' + u.team : ''}`], ['mine', 'My records']];
  return [['mine', 'My records']];
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  const [scope, setScopeState] = useState<Scope>(() => (sessionStorage.getItem('cbs.scope') as Scope) || 'all');
  const [idleWarn, setIdleWarn] = useState(false);
  const lastActive = useRef(Date.now());
  const notify = useToast();
  const navigate = useNavigate();

  const options = scopeOptionsFor(user);
  const effectiveScope = options.some(o => o[0] === scope) ? scope : options[0][0];

  const logout = useCallback((reason: 'idle' | 'expired' | 'manual' = 'manual') => {
    if (tokenStore.get() && reason === 'manual') api('/auth/logout', { method: 'POST' }).catch(() => {});
    tokenStore.clear();
    setUser(null);
    setIdleWarn(false);
    navigate('/login');
    if (reason === 'idle') notify('info', 'Session expired', 'You were signed out after 30 minutes of inactivity. Sign in again to continue.');
    else if (reason === 'manual') notify('info', 'Signed out', 'You have been signed out.');
  }, [navigate, notify]);

  const refresh = useCallback(async () => {
    if (!tokenStore.get()) { setLoading(false); return; }
    try { setUser(await api<User>('/auth/me')); }
    catch { tokenStore.clear(); setUser(null); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { refresh(); }, [refresh]);

  // Any 401 from the API (expired token, deactivated account) signs the user out with the server's message.
  useEffect(() => {
    const h = (e: Event) => {
      const err = (e as CustomEvent<ApiError>).detail;
      if (!tokenStore.get()) return;
      tokenStore.clear(); setUser(null); navigate('/login');
      notify(err.code === 'ACCOUNT_DEACTIVATED' ? 'error' : 'info', err.title, err.detail);
    };
    window.addEventListener('cbs:unauthorized', h);
    return () => window.removeEventListener('cbs:unauthorized', h);
  }, [navigate, notify]);

  // Idle timeout: warn at 25 minutes, sign out at 30.
  useEffect(() => {
    if (!user) return;
    const touch = () => { lastActive.current = Date.now(); };
    const evs = ['click', 'keydown', 'mousemove', 'touchstart'];
    evs.forEach(e => window.addEventListener(e, touch, { passive: true }));
    const t = setInterval(() => {
      const idle = Date.now() - lastActive.current;
      if (idle > IDLE_OUT_MS) logout('idle');
      else if (idle > IDLE_WARN_MS) setIdleWarn(true);
    }, 15_000);
    return () => { evs.forEach(e => window.removeEventListener(e, touch)); clearInterval(t); };
  }, [user, logout]);

  const completeLogin = (res: { token: string; user: User }) => {
    tokenStore.set(res.token);
    setUser(res.user);
    lastActive.current = Date.now();
    return res.user;
  };
  const login = async (email: string, password: string) =>
    completeLogin(await api<{ token: string; user: User }>('/auth/login', { method: 'POST', body: { email, password } }));

  const value: AuthState = {
    user, loading, scope: effectiveScope, scopeOptions: options,
    setScope: s => { sessionStorage.setItem('cbs.scope', s); setScopeState(s); },
    can: p => !!user?.permissions.includes(p),
    login, completeLogin, logout, refresh,
  };

  return (
    <Ctx.Provider value={value}>
      {children}
      {idleWarn && user && (
        <Modal title="Are you still there?" onClose={() => { lastActive.current = Date.now(); setIdleWarn(false); }}
          footer={<><button className="btn btn-outline" onClick={() => logout('manual')}>Sign out</button>
            <button className="btn btn-primary" onClick={() => { lastActive.current = Date.now(); setIdleWarn(false); notify('success', 'Session extended'); }}>Stay signed in</button></>}>
          <Alert type="warning" title="You'll be signed out in about 5 minutes">Because of inactivity. Unsaved form input will be lost.</Alert>
        </Modal>
      )}
    </Ctx.Provider>
  );
}
