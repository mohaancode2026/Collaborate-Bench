import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, InboxSummary } from './api';
import { useAuth } from './auth';
import { useToast } from './ui';

/** Alert preferences, per browser. */
export interface InboxAlertPrefs { sound: boolean; desktop: boolean; startHour: number; endHour: number; }
const PREFS_KEY = 'cbs.inboxAlerts';
const SEEN_KEY = 'cbs.inboxSeenHigh';
const DEFAULTS: InboxAlertPrefs = { sound: true, desktop: true, startHour: 9, endHour: 18 };

export function loadAlertPrefs(): InboxAlertPrefs {
  try { return { ...DEFAULTS, ...(JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}') as Partial<InboxAlertPrefs>) }; } catch { return DEFAULTS; }
}
export function saveAlertPrefs(p: InboxAlertPrefs) {
  try { localStorage.setItem(PREFS_KEY, JSON.stringify(p)); } catch { /* private mode */ }
  window.dispatchEvent(new Event('cbs:inbox'));
}

/** Monday–Friday between the start and end hour, in the browser's time zone. */
export function inWorkday(p: InboxAlertPrefs, d = new Date()) {
  const day = d.getDay();
  const h = d.getHours() + d.getMinutes() / 60;
  return day >= 1 && day <= 5 && h >= p.startHour && h < p.endHour;
}

/** A short two-tone chime (no audio file needed). Browsers play it only after the user has clicked somewhere on the page. */
export function playChime() {
  try {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return;
    const ctx = new Ctx();
    [[880, 0], [1320, 0.18]].forEach(([freq, at]) => {
      const o = ctx.createOscillator(); const g = ctx.createGain();
      o.type = 'sine'; o.frequency.value = freq;
      g.gain.setValueAtTime(0.0001, ctx.currentTime + at);
      g.gain.exponentialRampToValueAtTime(0.25, ctx.currentTime + at + 0.02);
      g.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + at + 0.35);
      o.connect(g).connect(ctx.destination);
      o.start(ctx.currentTime + at); o.stop(ctx.currentTime + at + 0.4);
    });
    setTimeout(() => ctx.close().catch(() => {}), 1200);
  } catch { /* audio blocked */ }
}

/**
 * Watches the AI Inbox (every minute, and on 'cbs:inbox'): the sidebar badge and, during the workday, a desktop
 * notification + chime + toast for each new high-priority email (FRD-003). Outside the workday the badge still updates.
 */
export function useInboxWatch() {
  const { user, can } = useAuth();
  const [summary, setSummary] = useState<InboxSummary | null>(null);
  const notify = useToast();
  const nav = useNavigate();
  const first = useRef(true);

  const alert = useCallback((s: InboxSummary) => {
    if (!s.latestHighId) return;
    let seen = 0;
    try { seen = Number(localStorage.getItem(SEEN_KEY) ?? 0); } catch { /* ignore */ }
    if (s.latestHighId <= seen) return;
    try { localStorage.setItem(SEEN_KEY, String(s.latestHighId)); } catch { /* ignore */ }
    // The very first time this browser sees the inbox, just remember where we are: no alert for old mail.
    if (seen === 0 && first.current) return;
    const p = loadAlertPrefs();
    if (!inWorkday(p)) return;
    const title = `High priority: ${s.latestHighSubject ?? 'new email'}`;
    const from = s.latestHighFrom ? `From ${s.latestHighFrom}` : '';
    notify('warning', title, `${from}${from ? ' · ' : ''}${s.high} high-priority email${s.high === 1 ? '' : 's'} in your AI Inbox.`);
    if (p.sound) playChime();
    if (p.desktop && 'Notification' in window && Notification.permission === 'granted') {
      try {
        const n = new Notification(title, { body: from || 'Open your AI Inbox', tag: `inbox-${s.latestHighId}`, icon: '/brand/collaborate-mark.png' });
        n.onclick = () => { window.focus(); nav(`/inbox?open=${s.latestHighId}`); n.close(); };
      } catch { /* some browsers only allow notifications from a service worker */ }
    }
  }, [notify, nav]);

  useEffect(() => {
    if (!user || !can('mail.view')) { setSummary(null); return; }
    let alive = true;
    const load = () => api<InboxSummary>('/inbox/summary')
      .then(s => { if (!alive) return; setSummary(s); alert(s); first.current = false; })
      .catch(() => {});
    load();
    const t = setInterval(load, 60_000);
    window.addEventListener('cbs:inbox', load);
    return () => { alive = false; clearInterval(t); window.removeEventListener('cbs:inbox', load); };
  }, [user, can, alert]);
  return summary;
}
