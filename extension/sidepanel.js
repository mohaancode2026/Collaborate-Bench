'use strict';
// Collaborate-Bench side panel for LinkedIn.
// Reads only the LinkedIn page the recruiter has open, and only when the panel is open; every save is a click.
// No crawling, no automatic messages or applications. All data goes to the recruiter's own Collaborate-Bench server.

const DEFAULT_SERVER = 'http://localhost:5173';
const $app = document.getElementById('app');
const $toast = document.getElementById('toast');

const state = {
  server: DEFAULT_SERVER, token: null, user: null, expires: null,
  page: null,        // what was read from the tab: { kind: 'profile' | 'job' | 'other', ... }
  pageKey: null,     // profile slug / job id, to skip re-loading the same page
  ctx: null,         // profile context or job context from the API
  tab: 'overview',
  options: { channels: [], outcomes: [] },
  reqs: null,        // open requirements (for the map / log pickers)
  busy: false,
  error: null,
};

// ---------------------------------------------------------------- helpers
const h = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const ago = iso => {
  const d = (Date.now() - new Date(iso).getTime()) / 1000;
  if (d < 90) return 'just now';
  if (d < 3600) return `${Math.round(d / 60)} min ago`;
  if (d < 86400) return `${Math.round(d / 3600)} h ago`;
  if (d < 172800) return 'yesterday';
  if (d < 86400 * 60) return `${Math.round(d / 86400)} days ago`;
  return new Date(iso).toLocaleDateString();
};
const when = iso => new Date(iso).toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
/** Expiry of a Collaborate-Bench key (the "exp" claim), so the panel can say when to reconnect. */
const jwtExpiry = t => {
  try { const p = JSON.parse(atob(t.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'))); return p.exp ? new Date(p.exp * 1000).toISOString() : null; }
  catch { return null; }
};
const scoreCls = s =>(s >= 75 ? 'hi' : s >= 55 ? 'mid' : 'lo');

function toast(msg, bad = false) {
  $toast.textContent = msg;
  $toast.className = 'toast' + (bad ? ' bad' : '');
  $toast.hidden = false;
  clearTimeout(toast.t);
  toast.t = setTimeout(() => { $toast.hidden = true; }, bad ? 6000 : 3000);
}

class ApiError extends Error {
  constructor(status, code, detail, errors) { super(detail); this.status = status; this.code = code; this.errors = errors; }
}

async function api(path, { method = 'GET', body } = {}) {
  let res;
  try {
    res = await fetch(state.server.replace(/\/+$/, '') + '/api' + path, {
      method,
      headers: { Accept: 'application/json', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...(state.token ? { Authorization: `Bearer ${state.token}` } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(0, 'NETWORK', `Can't reach ${state.server}. Check the server address in settings and that the server is running.`);
  }
  if (res.status === 204) return null;
  const data = (res.headers.get('content-type') || '').includes('json') ? await res.json().catch(() => null) : null;
  if (!res.ok) {
    const fields = data?.errors ? Object.values(data.errors).flat().join(' ') : '';
    const err = new ApiError(res.status, data?.code || `HTTP_${res.status}`, fields || data?.detail || `Request failed (${res.status}).`, data?.errors);
    if (res.status === 401 && path !== '/auth/login') { await signOut(false); state.error = 'Your session ended. Please sign in again.'; render(); }
    throw err;
  }
  return data;
}

const store = {
  get: keys => chrome.storage.local.get(keys),
  set: obj => chrome.storage.local.set(obj),
  remove: keys => chrome.storage.local.remove(keys),
};

// ---------------------------------------------------------------- reading the LinkedIn page
// Runs inside the LinkedIn tab (chrome.scripting). Reads only what is rendered on the page.
function readLinkedInPage() {
  const clean = s => (s || '').replace(/[ \t]+/g, ' ').replace(/\n{3,}/g, '\n\n').trim();
  const text = el => clean(el ? (el.innerText || el.textContent) : '');
  const q = sel => document.querySelector(sel);
  const spans = root => [...root.querySelectorAll('span[aria-hidden="true"]')].map(s => clean(s.textContent)).filter(Boolean);
  const section = id => { const a = document.getElementById(id); return a ? a.closest('section') : null; };
  const sectionText = (id, max) => {
    const s = section(id);
    if (!s) return '';
    const out = [];
    for (const t of spans(s)) {
      if (out[out.length - 1] === t || /^(show all|show more|see more|…\s*more|about|experience|education|skills)$/i.test(t)) continue;
      out.push(t);
    }
    return out.join('\n').slice(0, max);
  };
  const path = location.pathname;

  if (/^\/in\/[^/]+/.test(path)) {
    const main = q('main') || document.body;
    const name = text(main.querySelector('h1')) || document.title.split('|')[0].replace(/^\(\d+\)\s*/, '').trim();
    const headline = text(main.querySelector('.text-body-medium.break-words')) || text(main.querySelector('[data-generated-suggestion-target]'));
    const loc = text(main.querySelector('.text-body-small.inline.t-black--light.break-words')) || text(main.querySelector('.pv-text-details__left-panel .text-body-small'));
    const skillsSec = section('skills');
    let skills = skillsSec ? [...skillsSec.querySelectorAll('a[data-field="skill_card_skill_topic"] span[aria-hidden="true"], .hoverable-link-text.t-bold span[aria-hidden="true"]')]
      .map(s => clean(s.textContent)).filter(s => s && s.length <= 60) : [];
    skills = [...new Set(skills)].slice(0, 30);
    return {
      kind: 'profile', url: location.href, name, headline, location: loc,
      about: sectionText('about', 3000), experience: sectionText('experience', 7000), education: sectionText('education', 1500), skills,
    };
  }

  if (/^\/jobs\//.test(path)) {
    const title = text(q('.job-details-jobs-unified-top-card__job-title h1, .job-details-jobs-unified-top-card__job-title, .jobs-unified-top-card__job-title, .top-card-layout__title'))
      || text(q('main h1'));
    const company = text(q('.job-details-jobs-unified-top-card__company-name a, .job-details-jobs-unified-top-card__company-name, .jobs-unified-top-card__company-name, .topcard__org-name-link'));
    const primary = text(q('.job-details-jobs-unified-top-card__primary-description-container, .job-details-jobs-unified-top-card__tertiary-description-container, .jobs-unified-top-card__primary-description, .topcard__flavor--bullet'));
    const prefs = text(q('.job-details-preferences-and-skills, .job-details-fit-level-preferences, .job-details-jobs-unified-top-card__job-insight')) + ' ' + primary;
    const workplace = /\bremote\b/i.test(prefs) ? 'Remote' : /\bhybrid\b/i.test(prefs) ? 'Hybrid' : /\bon-?site\b/i.test(prefs) ? 'On-site' : '';
    const employmentType = (prefs.match(/full-time|contract|part-time|temporary|internship/i) || [''])[0];
    const description = text(q('#job-details, .jobs-description__content, .jobs-description-content__text, .show-more-less-html__markup, .description__text')).slice(0, 9000);
    return { kind: 'job', url: location.href, title, company, location: primary.split('·')[0].trim(), workplace, employmentType, description };
  }
  return { kind: 'other', url: location.href };
}

function keyOf(url) {
  try {
    const u = new URL(url);
    if (!/(^|\.)linkedin\.com$/i.test(u.hostname)) return null;
    const p = u.pathname.match(/^\/in\/([^/]+)/i);
    if (p) return 'in:' + decodeURIComponent(p[1]).toLowerCase();
    const j = u.pathname.match(/\/jobs\/view\/(?:[^/]*?-)?(\d{5,})/i) || u.search.match(/[?&]currentJobId=(\d{5,})/i);
    if (j) return 'job:' + j[1];
    return 'li:other';
  } catch { return null; }
}

async function readActiveTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  const key = tab?.url ? keyOf(tab.url) : null;
  if (!tab || !key) return { page: { kind: 'other', url: tab?.url || '' }, key: 'none' };
  if (key === 'li:other') return { page: { kind: 'other', url: tab.url, linkedin: true }, key };
  for (let attempt = 0; attempt < 4; attempt++) {
    try {
      const [{ result }] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: readLinkedInPage });
      const ready = result && (result.kind === 'profile' ? result.name : result.kind === 'job' ? (result.title || result.description) : true);
      if (ready) return { page: { ...result, url: tab.url }, key };   // the tab URL as Chrome reports it
    } catch (e) {
      return { page: { kind: 'other', url: tab.url, linkedin: true, error: 'Reload the LinkedIn tab, then reopen this panel.' }, key };
    }
    await new Promise(r => setTimeout(r, 900));   // LinkedIn renders after navigation; give it a moment
  }
  return { page: { kind: 'other', url: tab.url, linkedin: true, error: "The page hasn't finished loading. Press refresh when it has." }, key };
}

// ---------------------------------------------------------------- loading
async function loadPage(force = false) {
  if (!state.token) return;
  const { page, key } = await readActiveTab();
  if (!force && key === state.pageKey && state.ctx) return;
  state.page = page; state.pageKey = key; state.ctx = null; state.error = null; state.tab = 'overview';
  state.busy = true; render();
  try {
    if (page.kind === 'profile') state.ctx = await api('/linkedin/profile/lookup', { method: 'POST', body: page });
    else if (page.kind === 'job') state.ctx = await api('/linkedin/job/lookup', { method: 'POST', body: page });
    else state.ctx = { reminders: await api('/linkedin/reminders').catch(() => []) };
  } catch (e) { state.error = e.message; }
  state.busy = false; render();
}

async function ensureReqs() {
  if (state.reqs) return state.reqs;
  state.reqs = (await api('/requirements?status=Open')).slice(0, 300);
  return state.reqs;
}

async function act(fn, ok) {
  if (state.busy) return;
  state.busy = true; render();
  try { const r = await fn(); if (ok) toast(ok); return r; }
  catch (e) { toast(e.message, true); }
  finally { state.busy = false; render(); }
}

// ---------------------------------------------------------------- views
function render() {
  document.getElementById('refresh').hidden = !state.token;
  document.getElementById('menu').hidden = !state.token;
  if (!state.token) return renderSignIn();
  if (state.view === 'settings') return renderSettings();
  const p = state.page;
  if (!p || (state.busy && !state.ctx)) { $app.innerHTML = `<div class="empty"><span class="spinner"></span><p>Reading the page…</p></div>`; return; }
  if (state.error && !state.ctx) { $app.innerHTML = `<div class="card"><p class="err">${h(state.error)}</p><button class="btn" data-act="reload">Try again</button></div>`; return; }
  if (p.kind === 'profile') return renderProfile();
  if (p.kind === 'job') return renderJob();
  renderIdle();
}

function insightsHtml(list) {
  if (!list?.length) return '';
  const icon = { info: 'i', warn: '!', danger: '!!' };
  return `<div class="stack">${list.map(i => `<div class="insight ${h(i.level)}"><b>${icon[i.level] || 'i'}</b><span>${h(i.text)}</span></div>`).join('')}</div>`;
}

function renderSignIn() {
  $app.innerHTML = `
  <div class="card stack">
    <h2>Sign in</h2>
    <p class="muted small">Connect this panel to your Collaborate-Bench server. It reads the LinkedIn page you have open only when you use it.</p>
    ${state.error ? `<p class="err">${h(state.error)}</p>` : ''}
    <label>Server address<input id="server" value="${h(state.server)}" placeholder="https://bench.yourcompany.com"></label>
    <label>Work email<input id="email" type="email" autocomplete="username"></label>
    <label>Password<input id="password" type="password" autocomplete="current-password"></label>
    <button class="btn primary block" data-act="login">Sign in</button>
    <details>
      <summary class="small muted" style="cursor:pointer">Sign in with Microsoft? Use a connection key</summary>
      <div class="stack" style="margin-top:8px">
        <p class="small muted">In the portal, open <b>LinkedIn Leads</b> in the menu and press <b>Connect the extension</b>. Paste the key here.</p>
        <label>Connection key<textarea id="key" rows="3" placeholder="eyJ…"></textarea></label>
        <button class="btn block" data-act="key">Connect</button>
      </div>
    </details>
  </div>`;
}

function renderSettings() {
  $app.innerHTML = `
  <div class="card stack">
    <h2>Settings</h2>
    <dl class="kv"><dt>Signed in as</dt><dd>${h(state.user?.name)} (${h(state.user?.role)})</dd>
    <dt>Server</dt><dd>${h(state.server)}</dd>
    ${state.expires ? `<dt>Session ends</dt><dd>${h(when(state.expires))}</dd>` : ''}</dl>
    <div class="row"><a class="btn" href="${h(state.server)}/linkedin" target="_blank">Open the portal</a><button class="btn danger" data-act="logout">Sign out</button></div>
    <button class="link" data-act="back">← Back</button>
  </div>`;
}

function renderIdle() {
  const p = state.page;
  const rem = state.ctx?.reminders || [];
  $app.innerHTML = `
  <div class="card">
    <h2>${p?.linkedin ? 'Open a profile or a job' : 'Go to LinkedIn'}</h2>
    <p class="muted">${p?.error ? h(p.error) : p?.linkedin
      ? 'Open someone\'s LinkedIn profile to save them as a lead and log outreach, or a LinkedIn job post to save it as a requirement and see your bench matches.'
      : 'This panel works on LinkedIn profiles (linkedin.com/in/…) and job posts (linkedin.com/jobs/…).'}</p>
  </div>
  <div class="card"><h3>Your reminders</h3>${remindersHtml(rem, true)}</div>`;
}

function remindersHtml(list, showName) {
  if (!list.length) return '<p class="muted small">Nothing due. Reminders you set from a profile show here.</p>';
  return `<div class="list">${list.map(r => `
    <div class="item">
      <div class="row between"><span>${showName ? (r.linkedInUrl ? `<a href="${h(r.linkedInUrl)}" target="_blank"><b>${h(r.consultantName)}</b></a>` : `<b>${h(r.consultantName)}</b>`) : `<b>${h(r.note || 'Follow up')}</b>`}</span>
        <span class="chip ${r.overdue ? 'bad' : 'warn'}">${r.overdue ? 'Overdue' : 'Due'} ${h(when(r.dueUtc))}</span></div>
      ${showName && r.note ? `<span>${h(r.note)}</span>` : ''}
      <div class="row between"><span class="muted small">${r.mine ? 'Yours' : 'Set by ' + h(r.byName)}</span>
      ${r.mine ? `<span class="row"><button class="btn sm" data-act="rem-done" data-id="${r.id}">Done</button><button class="btn sm" data-act="rem-dismiss" data-id="${r.id}">Dismiss</button></span>` : ''}</div>
    </div>`).join('')}</div>`;
}

// ---------- profile
function renderProfile() {
  const p = state.page, ctx = state.ctx || {};
  if (!ctx.found) {
    $app.innerHTML = `
    <div class="card stack">
      <div><h2>${h(p.name)}</h2><div class="muted">${h(p.headline)}</div><div class="muted small">${h(p.location)}</div></div>
      ${insightsHtml(ctx.insights)}
      ${ctx.savedElsewhere ? '' : `
      <p class="small muted">Not in Collaborate-Bench yet. Saving creates a <b>lead</b> (kept off the bench until you move them there). AI reads the profile for title, skills and summary.</p>
      <div class="grid2"><label>Email (optional)<input id="lead-email" type="email"></label><label>Phone (optional)<input id="lead-phone"></label></div>
      <button class="btn primary block" data-act="save-lead" ${state.busy ? 'disabled' : ''}>${state.busy ? '<span class="spinner"></span> Saving…' : 'Save as lead'}</button>`}
      ${p.skills?.length ? `<div><h3>Skills on the page</h3><div class="skills">${p.skills.map(s => `<span class="chip grey">${h(s)}</span>`).join('')}</div></div>` : ''}
    </div>`;
    return;
  }
  const c = ctx.consultant;
  const tabs = [['overview', 'Overview'], ['activity', 'Activity', ctx.activity.length], ['log', 'Log'], ['reminders', 'Reminders', ctx.reminders.length], ['suggestions', 'Jobs', ctx.suggestions.length]];
  $app.innerHTML = `
  <div class="card stack">
    <div class="row between"><div class="grow"><h2>${h(c.name)}</h2><div class="muted">${h(c.title)}</div><div class="muted small">${h(c.location)}${c.visa ? ' · ' + h(c.visa) : ''}${c.exp ? ` · ${c.exp} yrs` : ''}</div></div>
    <span class="chip ${c.status === 'Lead' ? '' : 'ok'}">${h(c.status)}</span></div>
    ${ctx.engine === 'rules' && ctx.notice ? `<p class="small muted">${h(ctx.notice)}</p>` : ''}
    <div class="tabs">${tabs.map(([k, l, n]) => `<button class="${state.tab === k ? 'on' : ''}" data-tab="${k}">${l}${n ? `<span class="count">${n}</span>` : ''}</button>`).join('')}</div>
    <div id="tabbody">${profileTab(ctx)}</div>
  </div>`;
  if (state.tab === 'log' || state.tab === 'suggestions') fillReqPicker();
}

function profileTab(ctx) {
  const c = ctx.consultant;
  switch (state.tab) {
    case 'activity':
      if (!ctx.activity.length) return '<p class="empty">No activity yet. Use <b>Log</b> after you message or call them.</p>';
      return `<div class="timeline">${ctx.activity.map(a => `
        <div class="tl"><span class="dot ${h(a.kind)}"></span><div>
          <div><b>${h(a.channel)}</b>${a.outcome ? ` · ${h(a.outcome)}` : ''}${a.requirementTitle ? ` <span class="muted">for</span> ${h(a.requirementTitle)}` : ''}</div>
          ${a.note ? `<div>${h(a.note)}</div>` : ''}
          <div class="muted small">${h(a.byName)} · ${h(ago(a.createdUtc))}</div></div></div>`).join('')}</div>`;
    case 'log':
      return `
      <div class="stack">
        <label>Job (optional)<select id="log-req"><option value="">General (no specific job)</option></select></label>
        <div class="grid2">
          <label>How<select id="log-channel">${state.options.channels.map(x => `<option>${h(x)}</option>`).join('')}</select></label>
          <label>Outcome<select id="log-outcome"><option value="">(none)</option>${state.options.outcomes.map(x => `<option>${h(x)}</option>`).join('')}</select></label>
        </div>
        <label>Note<textarea id="log-note" maxlength="1000" placeholder="What was said, rate discussed, availability…"></textarea></label>
        <label>Remind me<select id="log-remind">
          <option value="">No reminder</option><option value="2h">In 2 hours</option><option value="tom">Tomorrow 10:00</option>
          <option value="3d">In 3 days</option><option value="1w">Next week</option><option value="custom">Pick a time…</option></select></label>
        <input id="log-remind-at" type="datetime-local" hidden>
        <button class="btn primary" data-act="log" ${state.busy ? 'disabled' : ''}>Save</button>
        <hr style="border:0;border-top:1px solid var(--line);width:100%">
        <div class="row"><span class="grow small muted">Or just link them to the job above:</span>
          <button class="btn sm" data-act="map" data-status="Mapped">Map as lead</button><button class="btn sm danger" data-act="map" data-status="Rejected">Reject</button></div>
      </div>`;
    case 'reminders':
      return `${remindersHtml(ctx.reminders, false)}
      <div class="stack" style="margin-top:10px"><h3>New reminder</h3>
        <div class="grid2"><input id="rem-at" type="datetime-local"><input id="rem-note" maxlength="300" placeholder="e.g. Call back about rate"></div>
        <button class="btn" data-act="rem-add">Add reminder</button></div>`;
    case 'suggestions':
      if (!ctx.suggestions.length) return '<p class="empty">No open requirements fit this profile well yet.</p>';
      return `<p class="small muted">Open requirements that fit, best first. Mapping makes them a lead for that job.</p>
      <div class="list">${ctx.suggestions.map(m => `
        <div class="item"><div class="row between"><span class="title">${h(m.requirementTitle)}</span><span class="score ${scoreCls(m.score)}">${m.score}%</span></div>
        <div class="muted small">${h(m.client)} · ${h(m.vendor)} · ${h(m.type)}${m.billRate ? ` · $${m.billRate}/hr` : ''}</div>
        <div class="small">${h(m.why)}</div>
        <div><button class="btn sm" data-act="map-req" data-req="${m.requirementId}">Map as lead</button></div></div>`).join('')}</div>`;
    default:
      return `
      ${insightsHtml(ctx.insights)}
      <div style="margin-top:10px"><h3>Jobs</h3>
      ${ctx.leads.length ? `<div class="list">${ctx.leads.map(l => `
        <div class="item">
          <div class="row between"><span class="title">${h(l.requirementTitle)}</span>
            <span class="chip ${l.status === 'Rejected' ? 'bad' : l.status === 'Submitted' ? 'ok' : ''}">${l.status === 'Submitted' ? 'Submitted · ' + h(l.stage) : h(l.status) + ' as lead'}</span></div>
          <div class="muted small">${h(l.client)} · ${h(ago(l.updatedUtc))} by ${h(l.byName)}${l.reason ? ' · ' + h(l.reason) : ''}</div>
          ${l.lastActivity ? `<div class="small">Last: ${h(l.lastActivity)} · ${h(ago(l.lastActivityUtc))}${l.activityCount > 1 ? ` · ${l.activityCount} touches` : ''}</div>` : ''}
          ${l.leadId && l.status !== 'Submitted' ? `<div class="row">${l.status === 'Rejected'
            ? `<button class="btn sm" data-act="map-req" data-req="${l.requirementId}">Map again</button>`
            : `<button class="btn sm danger" data-act="reject-req" data-req="${l.requirementId}">Reject</button>`}
            <button class="btn sm" data-act="unlink" data-id="${l.leadId}">Remove</button></div>` : ''}
        </div>`).join('')}</div>` : '<p class="muted small">Not linked to any job yet. See <b>Jobs</b> for open requirements that fit.</p>'}
      </div>
      <div style="margin-top:10px"><h3>Skills</h3><div class="skills">${(c.skills || []).map(s => `<span class="chip grey">${h(s)}</span>`).join('') || '<span class="muted small">None found</span>'}</div></div>
      ${c.summary ? `<p class="small">${h(c.summary)}</p>` : ''}
      <div class="row wrap" style="margin-top:6px">
        <button class="btn sm" data-act="refresh-profile">Update from page</button>
        <a class="btn sm" href="${h(state.server)}/${c.status === 'Lead' ? 'linkedin' : 'bench?q=' + encodeURIComponent(c.name)}" target="_blank">Open in portal</a>
      </div>`;
  }
}

async function fillReqPicker() {
  const sel = document.getElementById('log-req');
  if (!sel) return;
  try {
    const reqs = await ensureReqs();
    const linked = new Set((state.ctx?.leads || []).map(l => l.requirementId));
    const top = reqs.filter(r => linked.has(r.id)), rest = reqs.filter(r => !linked.has(r.id));
    const opt = r => `<option value="${r.id}">${h(r.title)} · ${h(r.client)}</option>`;
    sel.insertAdjacentHTML('beforeend', (top.length ? `<optgroup label="Linked jobs">${top.map(opt).join('')}</optgroup>` : '') + `<optgroup label="All open jobs">${rest.map(opt).join('')}</optgroup>`);
  } catch (e) { toast(e.message, true); }
}

function remindAt(choice, custom) {
  const d = new Date();
  if (choice === '2h') d.setHours(d.getHours() + 2);
  else if (choice === 'tom') { d.setDate(d.getDate() + 1); d.setHours(10, 0, 0, 0); }
  else if (choice === '3d') { d.setDate(d.getDate() + 3); d.setHours(10, 0, 0, 0); }
  else if (choice === '1w') { d.setDate(d.getDate() + 7); d.setHours(10, 0, 0, 0); }
  else if (choice === 'custom') return custom ? new Date(custom).toISOString() : null;
  else return null;
  return d.toISOString();
}

// ---------- job
function renderJob() {
  const p = state.page, ctx = state.ctx || {};
  const matches = ctx.matches || [];
  const matchList = matches.length ? `<div class="list">${matches.map(m => `
    <div class="item"><div class="row between"><b>${h(m.consultantName)}</b><span class="score ${scoreCls(m.score)}">${m.score}%</span></div>
    <div class="muted small">${h(m.consultantTitle)} · ${h(m.visa || 'visa ?')}${m.payRate ? ` · $${m.payRate}/hr` : ''} · ${h(m.ownerName)}${m.submittedStage ? ` · <span class="chip ok">${h(m.submittedStage)}</span>` : ''}</div>
    <div class="small">${h(m.why)}</div></div>`).join('')}</div>` : '<p class="muted small">No bench consultants score 40% or more for this job.</p>';

  if (ctx.found) {
    const r = ctx.requirement;
    $app.innerHTML = `
    <div class="card stack">
      <div class="row between"><div class="grow"><h2>${h(r.title)}</h2><div class="muted">${h(r.client)} · ${h(r.vendor)}</div></div><span class="chip ${r.status === 'Open' ? 'ok' : 'grey'}">${h(r.status)}</span></div>
      <dl class="kv"><dt>Location</dt><dd>${h(r.location)} · ${h(r.mode)}</dd><dt>Type</dt><dd>${h(r.type)}${r.rate ? ` · $${r.rate}/hr` : ' · rate not posted'}</dd><dt>Visas</dt><dd>${h((r.visas || []).join(', '))}</dd></dl>
      <div class="skills">${(r.skills || []).map(s => `<span class="chip grey">${h(s)}</span>`).join('')}</div>
      ${insightsHtml(ctx.insights)}
      <div class="row"><a class="btn sm" href="${h(state.server)}/match" target="_blank">Open Match Agent</a><a class="btn sm" href="${h(state.server)}/jobs" target="_blank">Open Jobs</a></div>
    </div>
    <div class="card"><h3>Bench matches</h3>${matchList}</div>`;
    return;
  }
  const d = ctx.draft || {};
  const sel = (id, opts, v) => `<select id="${id}">${opts.map(o => `<option ${o === v ? 'selected' : ''}>${h(o)}</option>`).join('')}</select>`;
  $app.innerHTML = `
  <div class="card stack">
    <div><h2>${h(p.title || d.title)}</h2><div class="muted">${h(p.company)} · ${h(p.location)}</div></div>
    ${insightsHtml(ctx.insights)}
    <p class="small muted">Not saved yet. ${ctx.engine === 'claude' ? 'AI' : 'The rules engine'} filled this in from the post${ctx.notice ? ` (${h(ctx.notice)})` : ''}. Check it, then save it as a requirement.</p>
    <label>Title<input id="j-title" value="${h(d.title)}"></label>
    <div class="grid2"><label>End client<input id="j-client" value="${h(d.client)}"></label><label>Vendor<input id="j-vendor" value="${h(d.vendor)}"></label></div>
    <div class="grid2"><label>Location<input id="j-location" value="${h(d.location)}"></label><label>Work mode${sel('j-mode', ['Remote', 'Hybrid', 'Onsite'], d.mode)}</label></div>
    <div class="grid2"><label>Contract${sel('j-type', ['C2C', 'W2', '1099'], d.type)}</label><label>Bill rate $/hr (0 = unknown)<input id="j-rate" type="number" min="0" max="400" value="${h(d.rate || 0)}"></label></div>
    <label>Vendor tier${sel('j-tier', ['Direct', 'Prime vendor', 'Implementation partner', 'Tier-1', 'Tier-2'], d.tier)}</label>
    <label>Skills (comma separated)<input id="j-skills" value="${h((d.skills || []).join(', '))}"></label>
    <label>Visas accepted (comma separated, or Any)<input id="j-visas" value="${h((d.visas || ['Any']).join(', '))}"></label>
    <button class="btn primary block" data-act="save-job" ${state.busy ? 'disabled' : ''}>${state.busy ? '<span class="spinner"></span> Saving…' : 'Save as requirement'}</button>
  </div>
  <div class="card"><h3>Bench matches (preview)</h3>${matchList}</div>`;
}

// ---------------------------------------------------------------- actions
const val = id => document.getElementById(id)?.value?.trim() ?? '';
const list = s => s.split(',').map(x => x.trim()).filter(Boolean);

async function signIn(token, user, expires) {
  Object.assign(state, { token, user, expires, error: null, view: null, reqs: null });
  await store.set({ server: state.server, token, user, expires });
  state.options = await api('/linkedin/options').catch(() => state.options);
  await loadPage(true);
}

async function signOut(callServer = true) {
  if (callServer && state.token) api('/auth/logout', { method: 'POST' }).catch(() => {});
  Object.assign(state, { token: null, user: null, expires: null, ctx: null, page: null, pageKey: null, view: null, reqs: null });
  await store.remove(['token', 'user', 'expires']);
}

async function useServer(raw) {
  let url;
  try { url = new URL(raw.includes('://') ? raw : 'https://' + raw); } catch { throw new Error('Enter the server address, e.g. https://bench.yourcompany.com'); }
  const origin = url.origin;
  // Ask right away: Chrome only shows the prompt while the click is still "fresh" (no awaits before this call).
  // It resolves true straight away when access was already granted.
  let granted = false;
  try { granted = await chrome.permissions.request({ origins: [origin + '/*'] }); } catch { granted = false; }
  if (!granted) throw new Error(`Allow access to ${origin} so the panel can talk to it (Chrome asks when you sign in).`);
  state.server = origin;
}

const handlers = {
  async login() {
    try {
      await useServer(val('server'));
      const r = await api('/auth/login', { method: 'POST', body: { email: val('email'), password: document.getElementById('password').value } });
      await signIn(r.token, r.user, r.expiresUtc);
    } catch (e) { state.error = e.message; render(); }
  },
  async key() {
    try {
      await useServer(val('server'));
      state.token = val('key').replace(/^Bearer\s+/i, '');
      const user = await api('/auth/me');
      await signIn(state.token, user, jwtExpiry(state.token));
    } catch (e) { state.token = null; state.error = e.status === 401 ? 'That key is invalid or expired. Create a new one in the portal.' : e.message; render(); }
  },
  async logout() { await signOut(); render(); },
  back() { state.view = null; render(); },
  reload() { loadPage(true); },
  async 'save-lead'() {
    const body = { ...state.page, email: val('lead-email') || null, phone: val('lead-phone') || null };
    const ctx = await act(() => api('/linkedin/profile', { method: 'POST', body }), 'Saved as a lead');
    if (ctx) { state.ctx = ctx; state.tab = 'suggestions'; render(); }
  },
  async 'refresh-profile'() {
    const ctx = await act(() => api('/linkedin/profile', { method: 'POST', body: state.page }), 'Updated from the page');
    if (ctx) { state.ctx = ctx; render(); }
  },
  async map(el) {
    const req = val('log-req');
    if (!req) return toast('Pick a job first.', true);
    await setLead(Number(req), el.dataset.status);
  },
  async 'map-req'(el) { await setLead(Number(el.dataset.req), 'Mapped'); },
  async 'reject-req'(el) {
    const reason = prompt('Why is this candidate rejected for the job? (optional)') ?? null;
    await setLead(Number(el.dataset.req), 'Rejected', reason);
  },
  async unlink(el) {
    const ctx = await act(() => api(`/linkedin/leads/${el.dataset.id}`, { method: 'DELETE' }), 'Removed from the job');
    if (ctx) { state.ctx = ctx; render(); }
  },
  async log() {
    const choice = val('log-remind');
    const remind = remindAt(choice, val('log-remind-at'));
    if (choice === 'custom' && !remind) return toast('Pick the reminder time.', true);
    const body = {
      consultantId: state.ctx.consultant.id, requirementId: val('log-req') ? Number(val('log-req')) : null,
      channel: val('log-channel'), outcome: val('log-outcome') || null, note: val('log-note') || null, remindUtc: remind,
    };
    const ctx = await act(() => api('/linkedin/activities', { method: 'POST', body }), remind ? 'Saved, reminder set' : 'Saved');
    if (ctx) { state.ctx = ctx; state.tab = 'activity'; render(); }
  },
  async 'rem-add'() {
    const at = val('rem-at');
    if (!at) return toast('Pick when to be reminded.', true);
    const r = await act(() => api('/linkedin/reminders', { method: 'POST', body: { consultantId: state.ctx.consultant.id, dueUtc: new Date(at).toISOString(), note: val('rem-note') || null } }), 'Reminder set');
    if (r) { state.ctx.reminders = [...state.ctx.reminders, r].sort((a, b) => a.dueUtc.localeCompare(b.dueUtc)); render(); }
  },
  async 'rem-done'(el) { await closeReminder(el.dataset.id, 'Done'); },
  async 'rem-dismiss'(el) { await closeReminder(el.dataset.id, 'Dismissed'); },
  async 'save-job'() {
    const body = {
      url: state.page.url,
      requirement: {
        title: val('j-title'), client: val('j-client'), vendor: val('j-vendor'), tier: val('j-tier'), location: val('j-location'),
        mode: val('j-mode'), type: val('j-type'), rate: Number(val('j-rate') || 0), skills: list(val('j-skills')),
        visas: list(val('j-visas')).length ? list(val('j-visas')) : ['Any'], description: state.page.description, source: 'LinkedIn',
      },
    };
    const ctx = await act(() => api('/linkedin/job', { method: 'POST', body }), 'Saved as a requirement');
    if (ctx) { state.ctx = ctx; state.reqs = null; render(); }
  },
};

async function setLead(requirementId, status, reason = null) {
  const ctx = await act(() => api('/linkedin/leads', { method: 'POST', body: { consultantId: state.ctx.consultant.id, requirementId, status, reason } }),
    status === 'Mapped' ? 'Mapped as lead' : 'Marked rejected');
  if (ctx) { state.ctx = ctx; state.tab = 'overview'; render(); }
}

async function closeReminder(id, status) {
  const r = await act(() => api(`/linkedin/reminders/${id}`, { method: 'PATCH', body: { status } }), status === 'Done' ? 'Reminder done' : 'Reminder dismissed');
  if (!r || !state.ctx) return;
  if (state.ctx.reminders) state.ctx.reminders = state.ctx.reminders.filter(x => x.id !== r.id);
  render();
}

// ---------------------------------------------------------------- events
document.addEventListener('click', e => {
  const t = e.target.closest('[data-act],[data-tab]');
  if (!t) return;
  if (t.dataset.tab) { state.tab = t.dataset.tab; render(); return; }
  const fn = handlers[t.dataset.act];
  if (fn) { e.preventDefault(); fn(t); }
});
document.addEventListener('change', e => {
  if (e.target.id === 'log-remind') document.getElementById('log-remind-at').hidden = e.target.value !== 'custom';
});
document.addEventListener('keydown', e => {
  if (e.key === 'Enter' && (e.target.id === 'password' || e.target.id === 'email')) handlers.login();
});
document.getElementById('refresh').addEventListener('click', () => { state.reqs = null; loadPage(true); });
document.getElementById('menu').addEventListener('click', () => { state.view = state.view === 'settings' ? null : 'settings'; render(); });

// Follow the recruiter around LinkedIn: re-read when the active tab or its URL changes (LinkedIn is a single-page app).
let pending;
const follow = () => { clearTimeout(pending); pending = setTimeout(() => { if (state.token && state.view !== 'settings') loadPage(false); }, 700); };
chrome.tabs.onActivated.addListener(follow);
chrome.tabs.onUpdated.addListener((_id, info, tab) => { if (tab.active && (info.url || info.status === 'complete')) follow(); });
chrome.windows.onFocusChanged.addListener(follow);

// ---------------------------------------------------------------- start
(async () => {
  const s = await store.get(['server', 'token', 'user', 'expires']);
  state.server = s.server || DEFAULT_SERVER;
  if (s.token && (!s.expires || new Date(s.expires) > new Date())) {
    state.token = s.token; state.user = s.user; state.expires = s.expires;
    state.options = await api('/linkedin/options').catch(() => state.options);
    await loadPage(true);
  } else {
    if (s.token) state.error = 'Your session ended. Please sign in again.';
    render();
  }
})();
