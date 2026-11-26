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

/** The team works the US shift, so alert hours are US Eastern time (New York, daylight saving included) whatever the computer's zone. */
export const SHIFT_TZ = 'America/New_York';
const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** Weekday (0 = Sunday), hour as a fraction, and a label like "Mon 10:42 AM ET", for US Eastern time. */
export function easternNow(d = new Date()) {
  const parts = Object.fromEntries(new Intl.DateTimeFormat('en-US', { timeZone: SHIFT_TZ, weekday: 'short', hour: 'numeric', minute: '2-digit', hourCycle: 'h23' })
    .formatToParts(d).map(x => [x.type, x.value]));
  const hour = Number(parts.hour) % 24, minute = Number(parts.minute);
  const label = `${parts.weekday} ${d.toLocaleTimeString('en-US', { timeZone: SHIFT_TZ, hour: 'numeric', minute: '2-digit' })} ET`;
  return { day: DAYS.indexOf(parts.weekday), h: hour + minute / 60, label };
}

/**
 * Inside the shift: Monday–Friday (US Eastern) between the start and end hour, US Eastern time. An end before the start means the
 * shift runs past midnight (e.g. 20:00–4:00); its after-midnight part belongs to the weekday it started on.
 */
export function inWorkday(p: InboxAlertPrefs, d = new Date()) {
  const { day, h } = easternNow(d);
  const weekday = (x: number) => x >= 1 && x <= 5;
  if (p.startHour < p.endHour) return weekday(day) && h >= p.startHour && h < p.endHour;
  if (h >= p.startHour) return weekday(day);
  return h < p.endHour && weekday((day + 6) % 7);
}

/** "9:00 AM" style label for an hour of the day. */
export const hourLabel = (h: number) => `${((h + 11) % 12) + 1}:00 ${h % 24 < 12 ? 'AM' : 'PM'}`;

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

/** How often an open, visible tab asks the server to read the user's Outlook. The server skips it if it read it in the last 10 s. */
const MAIL_REFRESH_MS = 20_000;
let refreshing: Promise<void> | null = null;

/**
 * Asks the server to read the user's Outlook now (unless it just did), then tells every open page to reload ('cbs:inbox').
 * Calls made while one is running share it.
 */
export function refreshMail(): Promise<void> {
  // Reload either way: when the server skipped the read, its own scheduled sync may just have brought new mail.
  refreshing ??= api<{ synced: boolean }>('/inbox/refresh', { method: 'POST' })
    .then(() => { window.dispatchEvent(new Event('cbs:inbox')); })
    .catch(() => { /* offline or Outlook not connected: the scheduled server sync still runs */ })
    .finally(() => { refreshing = null; });
  return refreshing;
}

/**
 * Watches the AI Inbox: every 20 seconds while the tab is visible (and when it comes back into view) it has the server read
 * Outlook, then updates the sidebar badge and, during the workday, shows a desktop notification + chime + toast for each new
 * high-priority email (FRD-003). Outside the workday the badge still updates.
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
    // New mail: read Outlook now; when something was read, 'cbs:inbox' fires and reloads the badge and any open mail page.
    const tick = () => { if (document.visibilityState === 'visible') refreshMail(); };
    tick();
    const t = setInterval(tick, MAIL_REFRESH_MS);
    // Hidden tabs don't refresh; the badge still catches up every minute from the server's own 30-second sync.
    const slow = setInterval(() => { if (document.visibilityState !== 'visible') load(); }, 60_000);
    window.addEventListener('cbs:inbox', load);
    document.addEventListener('visibilitychange', tick);
    return () => {
      alive = false; clearInterval(t); clearInterval(slow);
      window.removeEventListener('cbs:inbox', load); document.removeEventListener('visibilitychange', tick);
    };
  }, [user, can, alert]);
  return summary;
}
