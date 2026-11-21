# Collaborate-Bench Sales

Bench sales platform for US IT staffing: AI resume parsing, job posting and sourcing agents, explainable matching, an RTR-safe submission pipeline, onboarding and billing, with a Delivery Manager → Team Lead → Recruiter hierarchy.

| Layer | Technology |
|---|---|
| Website | React 18 + TypeScript (Vite), React Router |
| API | ASP.NET Core 8 Web API (C#) |
| Database | SQL Server 2022+ / Azure SQL (row-level security, stored procedures) |
| AI | Anthropic Claude (Messages API with tool use), called only from the server |

```
collaborate-bench/
├─ database/      01_schema.sql (tables, RLS, procedures) · 02_seed.sql (demo org + data)
├─ backend/CollaborateBench.Api/
│  ├─ Security/        JWT, permission policies, 401/403 messages
│  ├─ Infrastructure/  SQL access (sets the user on every connection), error handling
│  ├─ Services/        scoped data reads, matching, insights, audit
│  ├─ Ai/              Claude client, AI features, built-in fallback engine
│  └─ Controllers/     auth, users, consultants, requirements, match, submissions, dashboard, ai, activity
├─ frontend/      React app (pages/, components.tsx, auth.tsx, api.ts, ui.tsx)
├─ Dockerfile, docker-compose.yml
```

## Quick start (Docker)

```bash
echo "ANTHROPIC_API_KEY=sk-ant-..." > .env     # optional: turns on Claude
docker compose up --build
```
Open http://localhost:8080 and sign in with a demo account (password `Demo@123`):

| Email | Role | Sees |
|---|---|---|
| priya@collabbench.io | Delivery Manager | all teams |
| arjun@collabbench.io | Team Lead, Team Alpha | own + Alpha |
| kavya@collabbench.io | Team Lead, Team Beta | own + Beta |
| megha@collabbench.io | Recruiter, Alpha | own records |
| admin@collabbench.io | Admin | everything |
| vivek@collabbench.io | deactivated (shows that error) | |

## Run without Docker

1. **Database**: create a database `CollaborateBench`, then run `database/01_schema.sql` and `database/02_seed.sql` (SSMS, Azure Data Studio or `sqlcmd -i`).
   **SQL Server 2012:** `01_schema.sql` needs 2016+. Create the tables with the EF migration, then run `database/03_sqlserver2012.sql`
   (`sqlcmd -S <server> -d CollaborateBench -I -f 65001 -i 03_sqlserver2012.sql`). On 2012 the API uses `CONTEXT_INFO` instead of
   `sp_set_session_context`, and team visibility comes from EF Core query filters in `AppDbContext` instead of row-level security.
2. **API**:
   ```bash
   cd backend/CollaborateBench.Api
   dotnet user-secrets set "ConnectionStrings:Sql" "Server=...;Database=CollaborateBench;..."
   dotnet user-secrets set "Jwt:Key" "<random 32+ char secret>"
   dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."     # optional
   dotnet run                                                   # http://localhost:5080
   ```
   In Development, `Seed:SetDemoPasswords=true` gives seeded users the password `Demo@123`.
3. **Website**: `cd frontend && npm install && npm run dev` (http://localhost:5173, `/api` is proxied to the API).

For production, `npm run build` and copy `frontend/dist` into the API's `wwwroot` (the Dockerfile does this). One app then serves both.

## Roles, hierarchy and data security

- **Permissions** live in `sec.RolePermissions`. On sign-in they go into the token, and every endpoint carries `[RequirePerm("…")]`. A missing permission returns a 403 that names what was denied, and the attempt is written to the audit log.
- **Data visibility is enforced by SQL Server row-level security** (`sec.fn_CanAccessOwner`). The API tags each connection with the user (`sp_set_session_context`), so even a coding mistake can't return another team's consultants or submissions:
  - Recruiter: own rows only
  - Team Lead: own rows plus everyone below them (via the `sec.UserHierarchy` closure table)
  - Delivery Manager / Admin: all rows
- The **scope selector** in the top bar (My records / My team / All) narrows the view inside what RLS allows.
- **Business rules** in `bench.usp_MoveSubmissionStage`: MSA/PO and Billing need a Team Lead or above, billing needs 6 compliance items done, and stale edits are rejected with optimistic concurrency.
- **Data rules**: RTR is required, duplicate submissions and duplicate consultant emails are blocked, and the submit rate must be above the pay rate.

## Sign-in and account security

- Passwords are hashed with ASP.NET Core Identity's `PasswordHasher` (PBKDF2).
- The account locks after 5 wrong passwords, for 5 minutes (configurable). Login is also rate-limited per IP.
- An unknown email and a wrong password get the same message, so accounts can't be discovered.
- New users and password resets get a one-time temporary password. The user must change it at first sign-in (10+ characters, upper, lower, number, symbol).
- The website signs users out after 30 minutes of inactivity, with a warning at 25. Deactivating a user blocks their existing token within 60 seconds.
- Admins can unlock accounts and reset passwords from **Team & Access**.

## Error handling

Every API error has the same shape:

```json
{ "status": 409, "code": "DUPLICATE_SUBMISSION", "title": "Already submitted",
  "detail": "Ravi Kumar has already been submitted to \"Senior Java Developer\"…",
  "errors": { "submitRate": ["Must be higher than the consultant pay rate"] },
  "correlationId": "ERR-3F9A1C" }
```
- The website shows `errors` next to the fields, and `title` + `detail` as a toast.
- Unexpected errors show a reference (`ERR-…`). The same reference is in the server log.
- A 401 signs the user out with the reason ("Session expired", "Account deactivated").
- Database errors are translated (duplicate key, constraint, timeout, unavailable, outside your team).
- If Claude is down or rate-limited, the built-in engine answers and the UI says so.

Common codes: `VALIDATION`, `INVALID_CREDENTIALS`, `ACCOUNT_LOCKED`, `ACCOUNT_DEACTIVATED`, `PASSWORD_NOT_SET`, `SESSION_EXPIRED`, `PERMISSION_DENIED`, `NOT_FOUND`, `CONFLICT`, `DUPLICATE_CONSULTANT`, `DUPLICATE_SUBMISSION`, `DUPLICATE_REQUIREMENT`, `HAS_ACTIVE_SUBMISSIONS`, `ONBOARDING_INCOMPLETE`, `REQUIREMENT_CLOSED`, `RATE_LIMITED`, `DB_UNAVAILABLE`, `SERVER_ERROR`.

## AI features

All AI calls run on the server. The API key never reaches the browser, and every call is logged in `ai.Interactions` (feature, model, tokens, latency, errors).

| Feature | Where | What it does |
|---|---|---|
| Resume parser | AI Resume Parser | PDF/DOCX/TXT → structured profile, duplicate warning, top 3 matching jobs |
| Posting agent | Jobs → Posting agent | Writes the JD and queues it for channels (`bench.JobPostings`) |
| Sourcing agent | Jobs → Sourcing agent | Vendor email → structured requirement |
| Match agent | Match Agent, Dashboard | Explainable 0–100 score plus an AI-written submission pitch |
| Rate advice | Submit dialog | Suggests a submit rate between pay and bill rate |
| Interview prep | Submissions board | Question sheet and talking points for the consultant |
| Daily briefing | Dashboard | Risks and priorities for today |
| Copilot | Every page | Chat that uses tools to query live data, limited by RLS to what the user may see |

Without `Anthropic:ApiKey`, every feature still works through the built-in rules engine (`Ai/Rules.cs`).

## External jobs (LinkedIn, Indeed and more)

The **External Jobs** page searches outside job boards and scores every job against your bench. LinkedIn has no public job-search API, so the app uses **JSearch** on RapidAPI. JSearch returns Google for Jobs results, which include listings from LinkedIn, Indeed, Glassdoor, ZipRecruiter, Dice and company career sites.

**Setup**
1. Create a RapidAPI account, subscribe to the JSearch API (it has a free tier), and copy your key.
2. Store the key outside source control:
   `dotnet user-secrets set "JobFeed:RapidApiKey" "<key>" --project backend/CollaborateBench.Api`
   (or set the environment variable `JobFeed__RapidApiKey`).
3. Restart the API. Database tables come from the `ExternalJobs` EF migration.

**Settings (`JobFeed` in appsettings.json)**
| Setting | Default | Meaning |
|---|---|---|
| `DailyCallLimit` | 50 | API calls per day (UTC) for the whole company |
| `CacheHours` | 6 | Repeating the same search within this window reuses saved results without calling the API |
| `AutoRefreshHours` | 0 (off) | When set, searches for every bench consultant's title and location on this interval |
| `AutoRefreshMaxSearches` | 10 | Maximum searches per automatic run |
| `KeepDays` | 30 | Jobs that haven't appeared in a search for this many days are deleted (imported jobs are kept) |

**Applying a candidate:** choose the consultant, confirm the bill rate and RTR, and submit. The app does three things:
- It creates a normal requirement with source `External`.
- It creates the submission through the same rules as any other submission.
- It gives you the link to finish the application on the job board.

Searches send only the job title, skills and location, never consultant names or contact details. Visa rules, rates and contract type are read from the job description, so confirm them with the employer or vendor before you apply.

## Sign in with Microsoft and vendor email tracking

There are two ways to sign in:
- **Email + password:** works for any email address, as before.
- **Sign in with Microsoft (Outlook):** for company Microsoft 365 accounts. The account's email must already exist as a user in Team & Access, because nobody is created automatically. After signing in, that user's emails with vendors are tracked.

Password users can also link their Outlook mailbox from **Vendor Emails → Connect Outlook**.

**What gets tracked.** Only emails to or from a vendor in the **Vendor list** count. A vendor entry is either a domain (`teksystems.com`, which includes subdomains) or a full address, used for recruiters on Gmail or Yahoo. Free-mail domains and your own domain can't be added. For each email the app keeps:
- sender and recipients
- subject
- the first 200 characters
- attachment names
- the date
- any consultant named in it

The full body is never stored. Outgoing emails with no vendor reply in the same thread after 48 hours show as **Awaiting reply**. Recruiters see their own emails, Team Leads their team's, and managers and admins everyone's.

**Setup (Microsoft Entra ID)**
1. Entra admin center → App registrations → New registration. Choose single tenant (your company only).
2. Add a **Web** redirect URI: `http://localhost:5173/api/auth/microsoft/callback` for development, and your production URL plus `/api/auth/microsoft/callback`.
3. Certificates & secrets → New client secret.
4. API permissions → Microsoft Graph → Delegated: `openid`, `profile`, `email`, `offline_access`, `User.Read`, `Mail.Read`, `Mail.Send`. An admin should **Grant admin consent** so users aren't each asked to approve.
5. Store the settings outside source control:
   ```
   dotnet user-secrets set "Microsoft:TenantId" "<tenant id>" --project backend/CollaborateBench.Api
   dotnet user-secrets set "Microsoft:ClientId" "<application (client) id>" --project backend/CollaborateBench.Api
   dotnet user-secrets set "Microsoft:ClientSecret" "<secret value>" --project backend/CollaborateBench.Api
   ```
6. Set `Microsoft:CompanyDomains` to your own email domains, so mail from colleagues is never treated as vendor mail.
7. Set `DataProtection:KeysPath` to a folder that persists, and that every server shares if you run several. Outlook sign-ins are encrypted with these keys. If the keys are lost, users have to reconnect Outlook.
8. Restart the API. Database tables come from the `VendorMailTracking` EF migration.

**Sending email to vendors.** Click **New email** or **Reply** on Vendor Emails, or **Email** on the External Jobs apply screen. The email goes out from the user's own Outlook mailbox through Microsoft Graph, so it lands in their Sent Items and replies stay in the vendor's thread. There's an AI "submit a consultant" template and a follow-up template. Attachments are limited to 3 MB in total and executable files are blocked. Every send is recorded in the audit log, with recipients and subject but not the message text. Users who connected Outlook before `Mail.Send` was added are asked to reconnect once.

**Email templates.** Templates are managed under **Vendor Emails → Templates**:
- **Team templates** can be used by everyone and changed only by Team Lead and above (the `templates.manage` permission).
- **Personal templates** belong to each user.
- Six starter templates are included: submission, follow-up, rate confirmation, interview, onboarding and hotlist.

Templates use merge fields such as `{{contact.firstName}}`, `{{vendor.name}}`, `{{consultant.name}}`, `{{consultant.skills}}`, `{{requirement.title}}`, `{{requirement.rate}}` and `{{me.name}}`. The editor shows the full list and a live preview. The fields are filled in on the server using the normal team permissions, and the contact's name comes from their past emails. Consultant pay rates are deliberately not available as a field. Unknown fields are rejected when a template is saved. Each template shows how often it has been sent.

**Follow-up reminders.** When an email you sent to a vendor gets no reply in the same thread within `FollowUps:AutoAfterHours` (default 48, 0 = off), a reminder is created automatically. Threads quiet for more than `FollowUps:AutoMaxDays` (default 14) are skipped. You can also click ⏰ on any of your emails to set a reminder yourself, with quick picks (tomorrow 9 AM, in 2 days, next Monday) and a note. A reminder closes by itself when the vendor replies ("Vendor replied") or you send anything else in that thread ("Done"). You can also snooze it, mark it done (for example if you called instead) or dismiss it, and dismissed reminders don't come back. Due reminders show as a count next to Vendor Emails in the sidebar and as a banner on the dashboard. **Follow up** opens a reply in the thread with the follow-up template filled in. Team Leads and above can view their team's follow-ups.

`MailSyncMinutes` (default 10) controls how often mailboxes are checked. `FirstSyncDays` (default 30) controls how far back the first sync, and any vendor list change, reads.

## Bench sales Autopilot

**Overview → Autopilot** runs the bench sales process on a schedule. It's off by default, and Team Lead and above turn it on in Settings (the `autopilot.manage` permission).

1. **Find:** every Bench or Marketing consultant is matched against open requirements and saved external jobs. Only matches at or above the minimum match (default 70%) with an accepted visa are kept, up to 3 new jobs per consultant per day.
2. **Prepare:** the AI tailors the resume (truth-checked as in the resume builder) and suggests a submit rate. It finds the vendor contact from past vendor emails or the job posting, and drafts the email from the team's "Profile submission" template.
3. **Approve:** a recruiter reviews the rates and the email, then clicks **Approve & send RTR**. The consultant gets an email from the recruiter's Outlook with the tailored resume and a one-time link.
4. **RTR e-signature:** on the public page `/rtr/<token>`, the consultant sees the role, client, vendor and their own pay rate. They tick the statement and type their name to sign, or decline. The signature, time, IP and browser are stored. Links expire after `Autopilot:RtrValidHours` (72), and only a hash of the token is kept.
5. **Submit:** within a minute, the vendor email goes out from the approving recruiter's Outlook with the resume attached, and the submission is added to the pipeline with the RTR evidence in its notes.
6. **Follow up:** when a vendor hasn't replied in 48 hours, the Autopilot replies in the same thread, at most `MaxAutoFollowUps` times. After that, a person takes over.
7. **Read replies:** vendor replies are classified (interview, rejected, closed, needs info, rate) and linked to the submission, with one-click actions such as **Move to interview**, **Withdraw** or **Reply**.

**Email alerts.** Recruiters get Autopilot updates by email, combined into one email per run (never one per event). Categories:
- submissions ready for approval
- vendor replies
- RTR and submission updates
- problems (missing resume, failed send)
- automatic follow-ups (off by default)

Each person chooses their categories under **Autopilot → 🔔 Email alerts**. There's a company-wide switch in Autopilot settings. Alerts are sent from `Autopilot:NotifyFromEmail` (any Outlook-connected mailbox, for example a shared account), or otherwise from the recipient's own Outlook to themselves. If neither is available, the alert stays in the app only. Missing-resume alerts are sent at most once a day per consultant, and alerts that can't be sent within 2 days are dropped.

Nothing reaches a vendor without a recruiter's approval and the consultant's RTR. Requirements: Outlook connected with `Mail.Send` for the approving recruiters, consultant emails on file, and an original resume per consultant. Set `Autopilot:PublicUrl` to an address consultants can open from the internet, and `Autopilot:CompanyName` for the RTR statement.

## AI resume builder

**Bench → AI Resume Builder** tailors a consultant's resume to a client's requirement.

How it works:
1. Upload the consultant's current resume (PDF, DOCX or TXT). It becomes the *original*, the only source of facts.
2. Pick a requirement, or paste a job description.
3. Click **Tailor resume**.

Each version is saved, can be edited, and can be downloaded as Word (ATS-friendly single column), printed or saved as PDF, or attached to an email to the vendor from the portal.

**Truthfulness rules.** These resumes go to vendors and clients, and invented content is resume fraud.
- The AI may only reword, reorder and emphasise what the original says.
- Skills the job needs that the resume doesn't show are listed as **gaps**. They're never added.
- Every version is checked against the original on the server:
  - Unsupported skills and certifications are removed.
  - Unknown employers, years and numbers (like "40%"), and tools mentioned in bullets, are flagged as **Check before sending**.
- Recruiter edits are checked the same way when saved.
- The name and contact details always come from the consultant profile. Email and phone are hidden by default, because vendors get them after the RTR.

Without `Anthropic:ApiKey`, the built-in engine tailors the summary and skills and keeps the experience text exactly as written. Claude calls use the main model (`Anthropic:Model`), with server-side refusal fallbacks on the models that support them. A refusal or a cut-off answer falls back to the built-in engine with a notice. Uploads, tailoring and downloads are recorded in the audit log.

## API architecture and conventions

```
backend/CollaborateBench.Api
├── Controllers/      one controller per file: HTTP, permissions ([RequirePerm]), input validation. No SQL, no try/catch for normal errors.
├── Services/         business rules and integrations (matching, submissions, job feed, Outlook mail, follow-ups, merge fields)
├── Ai/               Claude client + built-in rules engine fallback
├── Data/             EF Core entities and AppDbContext (team visibility filters, SQL Server 2012 user context)
├── Models/           request/response types (DTOs). JSON names are camelCase versions of the property names.
├── Security/         bearer tokens, permission policies, the current user
├── Infrastructure/   errors, correlation ids, health checks, DI registration (ServiceRegistration.cs)
└── Migrations/       EF Core migrations (the source of truth for the schema)
```

**Request pipeline** (Program.cs):
1. Correlation id
2. Exception handler
3. Status-code pages
4. Static files and CORS
5. Authentication
6. Rate limits
7. Authorization
8. Controllers

**Errors.** Every error response is `application/problem+json`:

```json
{ "status": 409, "title": "Already submitted", "detail": "Ravi Kumar has already been submitted to ...",
  "instance": "/api/submissions", "code": "DUPLICATE_SUBMISSION", "correlationId": "REQ-7F3A91C2D4", "errors": { "field": ["message"] } }
```

- `code` is stable, so the frontend can switch on it. `title` and `detail` are written for users. `errors` holds per-field messages for forms.
- `correlationId` is also returned in the `X-Correlation-Id` header and appears on every log line for that request. A caller's own `X-Correlation-Id` is reused when it's safe.
- Expected problems are raised with `throw AppException...` or `Validator`. Controllers don't catch them. `GlobalExceptionHandler` maps everything else:

| What happened | Status | Code |
|---|---|---|
| Validation, malformed or wrong-type JSON | 400 | `VALIDATION` |
| Not signed in, or token expired/invalid | 401 | `UNAUTHENTICATED`, `SESSION_EXPIRED`, `INVALID_TOKEN` |
| Missing permission / outside your team | 403 | `PERMISSION_DENIED` |
| Record or endpoint not found | 404 | `NOT_FOUND`, `ENDPOINT_NOT_FOUND` |
| Duplicate, concurrent edit, business rule | 409 | e.g. `DUPLICATE_SUBMISSION`, `CONFLICT` |
| Upload too large | 413 | `PAYLOAD_TOO_LARGE` |
| Rate limit (with `Retry-After`) | 429 | `RATE_LIMITED` |
| Outside service failed | 502 | `UPSTREAM_ERROR`, `MAIL_ERROR`, `JOBFEED_ERROR` |
| Database unreachable (with `Retry-After`) | 503 | `DB_UNAVAILABLE` |
| Timeout | 504 | `TIMEOUT`, `DB_TIMEOUT` |
| Anything unexpected | 500 | `SERVER_ERROR` (the message only gives the reference, never internal details) |

**try/catch rules.** Only catch an exception when you can do something specific with it, and catch the narrowest type:
- Translate it into an `AppException` the user understands.
- Fall back to another path, for example the built-in engine when Claude is down.
- Keep a background loop running.

Every intentionally ignored exception has a comment saying why.

**Logging.** Each failure is logged once:
- 4xx at Information, without a stack trace.
- 5xx at Error, with the exception and the SQL error number when there is one.

The framework's and EF Core's duplicate error logs are switched off (appsettings `Logging`, and `ConfigureWarnings` in `AddPersistence`).

**API documentation (Swagger).** Open `http://localhost:5080/swagger` while the API runs in Development.
- Every endpoint is shown with its summary (from the XML `///` comments), the permission it needs, any rate limit, its request and response schemas, and the standard error responses.
- To try calls: run `POST /api/auth/login`, copy `token`, click **Authorize**, paste it, then use **Try it out**.
- The raw OpenAPI document is at `/swagger/v1/swagger.json`. Postman, Insomnia and client generators can import it.
- Outside Development the docs are off unless `Swagger:Enabled` is `true`. They list every endpoint, so keep them off on a public server.

**Health.**
- `GET /health` (liveness) checks only that the process is up.
- `GET /health/ready` (readiness) checks that SQL Server answers within 5 seconds, and returns 503 when it doesn't.

## What to build next

- **Job board posting**: `bench.JobPostings` is the queue. Add a background worker (Hangfire or Azure Functions) that posts to each board's API and updates `Status`.
- **Email intake**: connect a shared inbox through Microsoft Graph and send new vendor emails to the sourcing agent automatically.
- **Semantic search**: on SQL Server 2025 or Azure SQL, enable the commented `VECTOR` section in `01_schema.sql` and store embeddings for consultants and requirements.
- **Single sign-on**: replace password sign-in with Microsoft Entra ID; the permission model stays the same.
- **Resume storage**: save original files to Azure Blob Storage (`bench.Resumes.BlobUri`).
