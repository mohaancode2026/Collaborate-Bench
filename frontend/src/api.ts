// Typed API client. Every failure becomes an ApiError carrying the server's
// ProblemDetails fields (code, title, detail, field errors, correlation id).

export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    public title: string,
    public detail: string,
    public errors?: Record<string, string[]>,
    public correlationId?: string,
  ) {
    super(detail);
  }
}

const TOKEN_KEY = 'cbs.token';
export const tokenStore = {
  get: () => { try { return sessionStorage.getItem(TOKEN_KEY); } catch { return null; } },
  set: (t: string) => { try { sessionStorage.setItem(TOKEN_KEY, t); } catch { /* storage unavailable */ } },
  clear: () => { try { sessionStorage.removeItem(TOKEN_KEY); } catch { /* storage unavailable */ } },
};

type Query = Record<string, string | number | boolean | undefined | null>;

export async function api<T>(path: string, opts: { method?: string; body?: unknown; query?: Query } = {}): Promise<T> {
  const qs = new URLSearchParams();
  Object.entries(opts.query ?? {}).forEach(([k, v]) => { if (v !== undefined && v !== null && v !== '') qs.set(k, String(v)); });
  const url = '/api' + path + (qs.toString() ? `?${qs}` : '');
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json';
  const t = tokenStore.get();
  if (t) headers.Authorization = `Bearer ${t}`;

  let res: Response;
  try {
    res = await fetch(url, { method: opts.method ?? 'GET', headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body) });
  } catch {
    throw new ApiError(0, 'NETWORK', "Can't reach the server", 'Check your internet connection and try again. If the problem continues, the server may be down.');
  }
  if (res.status === 204) return undefined as T;

  const isJson = (res.headers.get('content-type') ?? '').includes('json');
  const data = isJson ? await res.json().catch(() => null) : await res.text().catch(() => '');
  if (!res.ok) throw toApiError(res, (isJson && data) ? data : {}, path);
  return data as T;
}

type Problem = { code?: string; title?: string; detail?: string; errors?: Record<string, string[]>; correlationId?: string };
function toApiError(res: Response, p: Problem, path: string): ApiError {
  const err = new ApiError(
    res.status,
    p.code ?? `HTTP_${res.status}`,
    p.title ?? (res.status >= 500 ? 'Server error' : 'Request failed'),
    p.detail ?? (res.status >= 500 ? 'The server had a problem. Please try again in a moment.' : 'The request could not be completed.'),
    p.errors,
    p.correlationId,
  );
  if (res.status === 401 && path !== '/auth/login') window.dispatchEvent(new CustomEvent('cbs:unauthorized', { detail: err }));
  return err;
}

/** POST multipart form data (file uploads). Errors are ApiErrors, like api(). */
export async function postForm<T>(path: string, form: FormData): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  const t = tokenStore.get();
  if (t) headers.Authorization = `Bearer ${t}`;
  let res: Response;
  try { res = await fetch('/api' + path, { method: 'POST', headers, body: form }); }
  catch { throw new ApiError(0, 'NETWORK', "Can't reach the server", 'Check your internet connection and try again. If the problem continues, the server may be down.'); }
  const isJson = (res.headers.get('content-type') ?? '').includes('json');
  const data = isJson ? await res.json().catch(() => null) : null;
  if (!res.ok) {
    if (res.status === 413) throw new ApiError(413, 'TOO_LARGE', 'Attachments too large', 'Keep attachments under 3 MB in total.');
    throw toApiError(res, data ?? {}, path);
  }
  return data as T;
}

/** GET a file with the user's token. Returns the bytes and the server's file name (Content-Disposition). */
export async function fetchFile(path: string, query: Query = {}): Promise<{ blob: Blob; name?: string }> {
  const qs = new URLSearchParams();
  Object.entries(query).forEach(([k, v]) => { if (v !== undefined && v !== null && v !== '') qs.set(k, String(v)); });
  const headers: Record<string, string> = {};
  const t = tokenStore.get();
  if (t) headers.Authorization = `Bearer ${t}`;
  let res: Response;
  try { res = await fetch('/api' + path + (qs.toString() ? `?${qs}` : ''), { headers }); }
  catch { throw new ApiError(0, 'NETWORK', "Can't reach the server", 'Check your internet connection and try again. If the problem continues, the server may be down.'); }
  if (!res.ok) {
    const isJson = (res.headers.get('content-type') ?? '').includes('json');
    throw toApiError(res, isJson ? await res.json().catch(() => ({})) : {}, path);
  }
  const raw = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(res.headers.get('content-disposition') ?? '')?.[1];
  return { blob: await res.blob(), name: raw ? decodeURIComponent(raw) : undefined };
}

/** GET a file (e.g. an Excel export) with the user's token and save it. Errors are ApiErrors, like api(). */
export async function download(path: string, query: Query, fallbackName: string): Promise<void> {
  const qs = new URLSearchParams();
  Object.entries(query).forEach(([k, v]) => { if (v !== undefined && v !== null && v !== '') qs.set(k, String(v)); });
  const headers: Record<string, string> = {};
  const t = tokenStore.get();
  if (t) headers.Authorization = `Bearer ${t}`;
  let res: Response;
  try {
    res = await fetch('/api' + path + (qs.toString() ? `?${qs}` : ''), { headers });
  } catch {
    throw new ApiError(0, 'NETWORK', "Can't reach the server", 'Check your internet connection and try again. If the problem continues, the server may be down.');
  }
  if (!res.ok) {
    const isJson = (res.headers.get('content-type') ?? '').includes('json');
    throw toApiError(res, isJson ? await res.json().catch(() => ({})) : {}, path);
  }
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(res.headers.get('content-disposition') ?? '')?.[1];
  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement('a');
  a.href = url;
  a.download = name ? decodeURIComponent(name) : fallbackName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// ---------- shared types (mirror the API DTOs) ----------
export interface User {
  id: number; name: string; email: string; role: string; dataScope: 'ALL' | 'TEAM' | 'OWN';
  team?: string; managerName?: string; permissions: string[]; mustChangePassword: boolean;
}
export interface Consultant {
  id: number; name: string; title: string; email: string; phone: string; visa: string; location: string; relocate: boolean;
  rate: number; exp: number; status: string; skills: string[]; summary: string; ownerId: number; ownerName: string;
  ownerTeam?: string; daysOnBench: number; activeSubmissions: number; rowVersion: string;
}
export interface Requirement {
  id: number; title: string; client: string; vendor: string; tier: string; location: string; mode: string; type: string;
  rate: number; skills: string[]; visas: string[]; description?: string; source: string; status: string;
  postedById: number; postedByName: string; postedUtc: string; daysOld: number; saved: boolean; activeSubmissions: number;
}
export interface Submission {
  id: number; consultantId: number; consultantName: string; consultantTitle: string; requirementId: number; title: string;
  client: string; vendor: string; type: string; billRate: number; stage: string; submitRate: number; payRate: number;
  marginPerHour?: number | null; matchScore?: number; rtr: boolean; notes?: string; ownerId: number; ownerName: string;
  daysInStage: number; createdUtc: string; rowVersion: string;
}
export interface Match {
  consultantId: number; consultantName: string; consultantTitle: string; visa: string; payRate: number; ownerName: string;
  requirementId: number; requirementTitle: string; client: string; vendor: string; type: string; billRate: number;
  score: number; hit: string[]; miss: string[]; why: string; visaOk: boolean; submittedStage?: string | null;
}
export interface Insight { type: 'risk' | 'opportunity' | 'info'; title: string; detail: string; actionLabel: string; actionPath: string; }
export interface Activity { id: number; userName: string; role: string; action: string; detail: string; outcome: string; createdUtc: string; }
export interface Dashboard {
  bench: number; marketing: number; openRequirements: number; requirementsToday: number; activeSubmissions: number;
  inInterviewOrOffer: number; billing: number; weeklyMargin?: number | null; stages: { stage: string; count: number }[];
  insights: Insight[]; topMatches: Match[]; activity: Activity[]; byRecruiter: RecruiterStat[];
}
export interface RecruiterStat {
  userId: number; name: string; role: string; team?: string | null; bench: number; marketing: number;
  stages: { stage: string; count: number }[]; total: number; active: number; placed: number; stale: number;
  weeklyMargin?: number | null; lastSubmissionUtc?: string | null;
}
export interface TeamMember {
  id: number; name: string; email: string; role: string; managerId?: number; managerName?: string; team?: string;
  active: boolean; locked: boolean; lastLoginUtc?: string; consultants: number; activeSubmissions: number; placed: number;
}
export interface ExternalJob {
  id: number; provider: string; publisher?: string | null; title: string; company: string; location: string; remote: boolean; mode: string;
  employmentType?: string | null; suggestedType: string; minRate?: number | null; maxRate?: number | null; skills: string[]; visas: string[];
  description?: string | null; applyUrl: string; applyOptions: { publisher: string; url: string; isDirect: boolean }[];
  onLinkedIn: boolean; contactEmails: string[]; postedUtc?: string | null; daysOld?: number | null; fetchedUtc: string;
  requirementId?: number | null; hidden: boolean; topScore: number; matches: Match[];
}
export interface ExternalSearchResult { searchId: number; query: string; location?: string | null; cached: boolean; found: number; new: number; jobs: ExternalJob[]; }
export interface JobFeedStatus { configured: boolean; provider: string; callsToday: number; dailyLimit: number; autoRefreshHours: number; lastSearchUtc?: string | null; storedJobs: number; }
export interface VendorInfo { id: number; name: string; domains: string[]; active: boolean; sent: number; received: number; lastContactUtc?: string | null; awaitingReply: number; }
export interface VendorEmail {
  id: number; sentUtc: string; direction: 'In' | 'Out'; vendorId: number; vendor: string; fromAddress: string; fromName?: string | null;
  toAddresses: string; subject: string; preview: string; attachments: string[]; consultantId?: number | null; consultantName?: string | null;
  userId: number; userName: string; awaitingReply: boolean;
}
export interface RecruiterMailStat { userId: number; name: string; sent: number; received: number; vendors: number; awaitingReply: number; lastContactUtc?: string | null; mailbox?: string | null; mailStatus?: string | null; }
export interface MailSummary { days: number; sent: number; received: number; vendorsContacted: number; awaitingReply: number; byVendor: VendorInfo[]; byRecruiter: RecruiterMailStat[]; }
export interface MailConnection { configured: boolean; connected: boolean; mailbox?: string | null; status?: string | null; lastSyncUtc?: string | null; lastError?: string | null; vendors: number; canSend: boolean; }
export interface EmailTemplate {
  id: number; name: string; category: string; subject: string; body: string; shared: boolean; builtIn: boolean; ownerName?: string | null;
  canEdit: boolean; needs: ('consultant' | 'requirement')[]; useCount: number; lastUsedUtc?: string | null; updatedUtc: string;
}
export interface MergeField { key: string; label: string; group: string; example: string; }
export interface RenderResult { subject: string; body: string; missing: string[]; unknown: string[]; }
export interface Reminder {
  id: number; status: 'Open' | 'Done' | 'Replied' | 'Dismissed'; dueUtc: string; overdue: boolean; note?: string | null; auto: boolean;
  createdUtc: string; closedUtc?: string | null; emailId: number; subject: string; preview: string; vendor: string; vendorId: number;
  toAddresses: string; sentUtc: string; daysWaiting: number; consultantId?: number | null; consultantName?: string | null;
  userId: number; userName: string; mine: boolean;
}
export interface ResumeDoc {
  name: string; headline: string; email?: string | null; phone?: string | null; location?: string | null; summary: string;
  skills: { category: string; items: string[] }[];
  experience: { company: string; title: string; location?: string | null; start?: string | null; end?: string | null; bullets: string[]; environment?: string | null }[];
  education: { degree: string; school?: string | null; year?: string | null }[];
  certifications: string[]; additionalText?: string | null;
}
export interface ResumeSource { resumeId: number; fileName?: string | null; text: string; uploadedUtc: string; uploadedBy?: string | null; }
export interface TailoredResume {
  id: number; consultantId: number; consultantName: string; requirementId?: number | null; name: string; targetTitle: string; targetClient?: string | null;
  doc: ResumeDoc; changes: string[]; gaps: string[]; warnings: string[]; matched: string[]; engine: 'claude' | 'rules'; notice?: string | null;
  hideContact: boolean; createdBy?: string | null; createdUtc: string; updatedUtc: string;
}
export interface AutopilotItem {
  id: number; status: string; statusLabel: string; consultantId: number; consultantName: string; consultantTitle: string; consultantEmail?: string | null;
  ownerId: number; ownerName: string; requirementId?: number | null; externalJobId?: number | null; jobTitle: string; client?: string | null; vendor?: string | null;
  score: number; why?: string | null; tailoredResumeId?: number | null; resumeWarnings: number; gaps: string[]; billRate?: number | null; submitRate?: number | null;
  payRate?: number | null; contractType?: string | null; vendorEmail?: string | null; emailSubject?: string | null; emailBody?: string | null; error?: string | null;
  approvedBy?: string | null; approvedUtc?: string | null; submissionId?: number | null; submittedUtc?: string | null;
  rtr?: { status: string; createdUtc: string; expiresUtc: string; respondedUtc?: string | null; signedName?: string | null; declineReason?: string | null } | null;
  createdUtc: string; updatedUtc: string;
}
export interface AutopilotSummary { enabled: boolean; lastRunUtc?: string | null; lastRunSummary?: string | null; found: number; needsResume: number; awaitingApproval: number; awaitingRtr: number; submittedThisWeek: number; repliesToReview: number; failed: number; }
export interface AutopilotSettings { enabled: boolean; minScore: number; maxNewPerConsultant: number; maxPreparePerRun: number; includeExternalJobs: boolean; autoFollowUp: boolean; maxAutoFollowUps: number; classifyReplies: boolean; runEveryMinutes: number; lastRunUtc?: string | null; lastRunSummary?: string | null; aiConfigured: boolean; canManage: boolean; emailNotifications: boolean; }
export interface AlertPrefs { enabled: boolean; approvals: boolean; vendorReplies: boolean; rtrUpdates: boolean; problems: boolean; followUps: boolean; sendsFrom?: string | null; canReceive: boolean; reason?: string | null; lastSentUtc?: string | null; }
export interface VendorReply { emailId: number; sentUtc: string; vendor: string; fromAddress: string; fromName?: string | null; subject: string; preview: string; intent: string; summary?: string | null; submissionId?: number | null; consultantId?: number | null; consultantName?: string | null; jobTitle?: string | null; stage?: string | null; userId: number; userName: string; }
export interface RtrView { status: string; consultantFirstName: string; jobTitle: string; client?: string | null; vendor?: string | null; location?: string | null; contractType?: string | null; payRate?: number | null; statement: string; recruiterName: string; companyName: string; expiresUtc: string; expired: boolean; }
export interface AiText { text: string; source: 'claude' | 'rules'; notice?: string; }

export const STAGES = ['Submitted', 'Vendor Screen', 'Client Interview', 'Offer', 'MSA / PO', 'Billing'];
export const ONBOARDING_STAGES = ['MSA / PO', 'Billing'];
export const VISAS = ['USC', 'GC', 'GC-EAD', 'H1B', 'H4-EAD', 'L2-EAD', 'OPT', 'CPT', 'STEM OPT', 'TN', 'EAD'];
export const CHANNELS = ['Dice', 'LinkedIn Jobs', 'Indeed', 'Monster', 'C2C Portals', 'Vendor Email', 'VMS Portal', 'Corp Website'];
