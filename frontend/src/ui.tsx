import { createContext, ReactNode, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { ApiError } from './api';

// ---------- Toasts ----------
type ToastType = 'success' | 'error' | 'warning' | 'info';
interface Toast { id: number; type: ToastType; title: string; detail?: string; }
const ToastCtx = createContext<(type: ToastType, title: string, detail?: string) => void>(() => {});
const ICON: Record<ToastType, string> = { success: '✓', error: '✕', warning: '!', info: 'i' };

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<Toast[]>([]);
  const seq = useRef(0);
  const notify = useCallback((type: ToastType, title: string, detail?: string) => {
    const id = ++seq.current;
    setItems(xs => [...xs.slice(-3), { id, type, title, detail }]);
    setTimeout(() => setItems(xs => xs.filter(x => x.id !== id)), type === 'error' || type === 'warning' ? 7000 : 4000);
  }, []);
  return (
    <ToastCtx.Provider value={notify}>
      {children}
      <div id="toasts" aria-live="polite">
        {items.map(t => (
          <div key={t.id} className={`toast t-${t.type}`} role={t.type === 'error' ? 'alert' : 'status'}>
            <span className="ti">{ICON[t.type]}</span>
            <div className="tb"><b>{t.title}</b>{t.detail && <p>{t.detail}</p>}</div>
            <button className="tx" aria-label="Dismiss" onClick={() => setItems(xs => xs.filter(x => x.id !== t.id))}>✕</button>
          </div>
        ))}
      </div>
    </ToastCtx.Provider>
  );
}
export const useToast = () => useContext(ToastCtx);

export type FieldErrors = Record<string, string[] | undefined>;

/** Shows any error the right way: field errors inline, everything else as a toast. */
export function useErrorHandler() {
  const notify = useToast();
  return useCallback((err: unknown, setFieldErrors?: (e: FieldErrors) => void) => {
    if (err instanceof ApiError) {
      if (err.status === 401 && err.code !== 'INVALID_CREDENTIALS') return; // handled globally (sign-out)
      if (err.errors && setFieldErrors) setFieldErrors(err.errors);
      const type: ToastType = err.status >= 500 || err.status === 0 || err.status === 403 ? 'error' : 'warning';
      notify(type, err.title, err.detail);
      return;
    }
    console.error(err);
    notify('error', 'Something went wrong', 'An unexpected error happened in the app. Please refresh and try again.');
  }, [notify]);
}

// ---------- Modal & confirm ----------
export function Modal({ title, onClose, children, footer, wide }: { title: string; onClose: () => void; children: ReactNode; footer?: ReactNode; wide?: boolean }) {
  useEffect(() => {
    const k = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, [onClose]);
  return (
    <div className="ov" onMouseDown={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={`modal${wide ? ' wide' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
        <div className="modal-h"><h3>{title}</h3><button className="x" onClick={onClose} aria-label="Close">✕</button></div>
        <div className="modal-b">{children}</div>
        <div className="modal-f">{footer ?? <button className="btn btn-outline" onClick={onClose}>Close</button>}</div>
      </div>
    </div>
  );
}

interface ConfirmOpts { title: string; message: string; okLabel?: string; danger?: boolean; }
const ConfirmCtx = createContext<(o: ConfirmOpts) => Promise<boolean>>(async () => false);
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<(ConfirmOpts & { resolve: (v: boolean) => void }) | null>(null);
  const confirm = useCallback((o: ConfirmOpts) => new Promise<boolean>(resolve => setState({ ...o, resolve })), []);
  const done = (v: boolean) => { state?.resolve(v); setState(null); };
  return (
    <ConfirmCtx.Provider value={confirm}>
      {children}
      {state && (
        <Modal title={state.title} onClose={() => done(false)}
          footer={<><button className="btn btn-outline" onClick={() => done(false)}>Cancel</button>
            <button className={`btn ${state.danger ? 'btn-danger' : 'btn-primary'}`} onClick={() => done(true)} autoFocus>{state.okLabel ?? 'Confirm'}</button></>}>
          <Alert type={state.danger ? 'error' : 'warning'} title={state.message} />
        </Modal>
      )}
    </ConfirmCtx.Provider>
  );
}
export const useConfirm = () => useContext(ConfirmCtx);

// ---------- Small components ----------
export function Alert({ type, title, children }: { type: ToastType; title: string; children?: ReactNode }) {
  return (
    <div className={`alert a-${type}`} role={type === 'error' ? 'alert' : 'status'}>
      <span className="ti">{ICON[type]}</span><div><b>{title}</b>{children && <div>{children}</div>}</div>
    </div>
  );
}

export function Field({ label, error, children, className }: { label?: ReactNode; error?: string[]; children: ReactNode; className?: string }) {
  return (
    <div className={`fld${error?.length ? ' err' : ''}${className ? ' ' + className : ''}`}>
      {label && <label>{label}</label>}
      {children}
      {error?.length ? <small className="ferr">{error.join(' ')}</small> : null}
    </div>
  );
}

const COLORS = ['#2563eb', '#8b5cf6', '#0891b2', '#059669', '#d97706', '#db2777', '#4f46e5', '#0f766e'];
export function Avatar({ name, size }: { name: string; size?: number }) {
  const color = COLORS[[...name].reduce((a, c) => a + c.charCodeAt(0), 0) % COLORS.length];
  const initials = name.split(/\s+/).map(w => w[0]).join('').slice(0, 2).toUpperCase();
  return <div className="av" style={{ background: color, ...(size ? { width: size, height: size, fontSize: Math.round(size / 2.6) } : {}) }}>{initials}</div>;
}

export const Ring = ({ score, size }: { score: number; size?: number }) => (
  <div className="ring" style={{ ['--p' as string]: score, ...(size ? { width: size, height: size } : {}) }}><b>{score}</b></div>
);

export const Spinner = ({ dark }: { dark?: boolean }) => <span className={`spin${dark ? ' d' : ''}`} />;

export const Empty = ({ children }: { children: ReactNode }) => <div className="empty">{children}</div>;

export const visaClass = (v: string) => (['USC', 'GC'].includes(v) ? 'g' : v?.includes('EAD') ? 'c' : 'v');
export const statusClass = (s: string) => (s === 'Placed' || s === 'Open' ? 'g' : s === 'Marketing' ? 'y' : s === 'Closed' ? 's' : 'c');
export const scoreClass = (n: number) => (n >= 75 ? 'g' : n >= 50 ? 'y' : 'r');
export const money = (n: number) => '$' + (Math.round(n * 100) / 100).toLocaleString(undefined, { maximumFractionDigits: 2 });
export const ago = (iso: string) => {
  const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (m < 1) return 'just now'; if (m < 60) return `${m}m ago`;
  const h = Math.round(m / 60); return h < 24 ? `${h}h ago` : `${Math.round(h / 24)}d ago`;
};

export async function copyText(text: string, notify: ReturnType<typeof useToast>) {
  try { await navigator.clipboard.writeText(text); notify('success', 'Copied to clipboard'); }
  catch { notify('warning', 'Copy blocked', 'Your browser blocked clipboard access. Select the text and copy it manually.'); }
}

export const AiBadge = ({ source }: { source?: string }) =>
  source ? <span className={`tag ${source === 'claude' ? 'g' : 'y'}`}>{source === 'claude' ? 'By Claude' : 'Built-in engine'}</span> : null;
