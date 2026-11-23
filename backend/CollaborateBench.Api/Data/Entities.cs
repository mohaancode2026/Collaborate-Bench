namespace CollaborateBench.Api.Data;

// Entity classes map 1:1 to the tables created by database/01_schema.sql.

public class Role
{
    public byte RoleId { get; set; }
    public string Name { get; set; } = "";
    public string DataScope { get; set; } = "OWN";   // ALL | TEAM | OWN
    public byte Rank { get; set; }
}

public class Permission
{
    public string PermissionCode { get; set; } = "";
    public string Description { get; set; } = "";
}

public class RolePermission
{
    public byte RoleId { get; set; }
    public string PermissionCode { get; set; } = "";
    public Role Role { get; set; } = null!;
}

public class Team
{
    public int TeamId { get; set; }
    public string Name { get; set; } = "";
}

public class User
{
    public int UserId { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public byte RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public int? ManagerUserId { get; set; }
    public User? Manager { get; set; }
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
    public string? PasswordHash { get; set; }
    public bool MustChangePassword { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public byte FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Closure table maintained by sec.usp_RebuildHierarchy (ancestor → every descendant, incl. self).</summary>
public class UserHierarchy
{
    public int AncestorUserId { get; set; }
    public int DescendantUserId { get; set; }
    public byte Depth { get; set; }
}

public class Consultant
{
    public int ConsultantId { get; set; }
    public int OwnerUserId { get; set; }
    public User Owner { get; set; } = null!;
    public string FullName { get; set; } = "";
    public string? Title { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? VisaStatus { get; set; }
    public string? Location { get; set; }
    public bool OpenToRelocate { get; set; }
    public decimal PayRate { get; set; }
    public byte? ExperienceYears { get; set; }
    public string Status { get; set; } = "Bench";
    public List<string> Skills { get; set; } = new();
    public string? HotlistSummary { get; set; }
    /// <summary>Normalized public profile URL (https://www.linkedin.com/in/slug), unique when set.</summary>
    public string? LinkedInUrl { get; set; }
    /// <summary>Where the record came from: Manual | LinkedIn.</summary>
    public string Source { get; set; } = "Manual";
    // ----- details vendors ask for on a submission -----
    /// <summary>Passport number, encrypted with ASP.NET Data Protection (never stored in clear text).</summary>
    public string? PassportProtected { get; set; }
    /// <summary>Last 4 characters of the passport number, for the masked display (••••1234).</summary>
    public string? PassportLast4 { get; set; }
    public DateTime? VisaValidUntil { get; set; }
    /// <summary>e.g. "Immediately", "2 weeks notice", "From Jan 5".</summary>
    public string? Availability { get; set; }
    public string? PreferredLocations { get; set; }
    /// <summary>Highest degree, school and year, e.g. "MS Computer Science, UNT, 2014".</summary>
    public string? Education { get; set; }
    public string? Certifications { get; set; }
    public byte? UsExperienceYears { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVer { get; set; } = Array.Empty<byte>();
}

public class Resume
{
    public int ResumeId { get; set; }
    public int ConsultantId { get; set; }
    public string? FileName { get; set; }
    public string? BlobUri { get; set; }
    public string ExtractedText { get; set; } = "";
    public string ParsedBy { get; set; } = "rules";
    public int UploadedByUserId { get; set; }
    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;
}

public class Requirement
{
    public int RequirementId { get; set; }
    public string Title { get; set; } = "";
    public string EndClient { get; set; } = "";
    public string Vendor { get; set; } = "Direct client";
    public string Tier { get; set; } = "Direct";
    public string Location { get; set; } = "Remote";
    public string WorkMode { get; set; } = "Hybrid";
    public string ContractType { get; set; } = "C2C";
    public decimal BillRate { get; set; }
    public List<string> Skills { get; set; } = new();
    public List<string> AcceptedVisas { get; set; } = new() { "Any" };
    public string? Description { get; set; }
    public string Source { get; set; } = "Manual";
    /// <summary>Page the requirement was captured from (e.g. https://www.linkedin.com/jobs/view/123/), unique when set.</summary>
    public string? SourceUrl { get; set; }
    public string Status { get; set; } = "Open";
    public int PostedByUserId { get; set; }
    public User PostedBy { get; set; } = null!;
    public DateTime PostedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVer { get; set; } = Array.Empty<byte>();
}

public class JobPosting
{
    public int PostingId { get; set; }
    public int RequirementId { get; set; }
    public string Channel { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class SavedRequirement
{
    public int UserId { get; set; }
    public int RequirementId { get; set; }
}

public class Submission
{
    public int SubmissionId { get; set; }
    public int ConsultantId { get; set; }
    public Consultant Consultant { get; set; } = null!;
    public int RequirementId { get; set; }
    public Requirement Requirement { get; set; } = null!;
    public int OwnerUserId { get; set; }
    public User Owner { get; set; } = null!;
    public string Stage { get; set; } = "Submitted";
    public decimal SubmitRate { get; set; }
    public decimal PayRate { get; set; }
    public byte? MatchScore { get; set; }
    public bool RtrSigned { get; set; }
    public string? Notes { get; set; }
    public DateTime StageChangedUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVer { get; set; } = Array.Empty<byte>();
}

public class SubmissionStageHistory
{
    public long HistoryId { get; set; }
    public int SubmissionId { get; set; }
    public string? FromStage { get; set; }
    public string ToStage { get; set; } = "";
    public int ChangedByUserId { get; set; }
    public DateTime ChangedUtc { get; set; } = DateTime.UtcNow;
}

public class OnboardingItem
{
    public int SubmissionId { get; set; }
    public string ItemCode { get; set; } = "";
    public bool IsDone { get; set; }
    public int? DoneByUserId { get; set; }
    public User? DoneBy { get; set; }
    public DateTime? DoneUtc { get; set; }
}

public class AuditLog
{
    public long AuditId { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public int? EntityId { get; set; }
    public string Outcome { get; set; } = "Success";
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class AiInteraction
{
    public long InteractionId { get; set; }
    public int? UserId { get; set; }
    public string Feature { get; set; } = "";
    public string Model { get; set; } = "";
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? LatencyMs { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A job pulled from an outside job-search API (e.g. JSearch, which aggregates LinkedIn, Indeed, Glassdoor…).
/// Shared across teams like requirements. Becomes a normal requirement when someone imports it or applies a candidate.</summary>
public class ExternalJob
{
    public int ExternalJobId { get; set; }
    public string Provider { get; set; } = "JSearch";
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Company { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsRemote { get; set; }
    public string? EmploymentType { get; set; }
    /// <summary>Where the listing was published (LinkedIn, Indeed, the company site…).</summary>
    public string? Publisher { get; set; }
    public string ApplyUrl { get; set; } = "";
    /// <summary>Every place the same job can be applied to (LinkedIn, Indeed, the employer's own site...).</summary>
    public List<ApplyOption> ApplyOptions { get; set; } = new();
    public string? Description { get; set; }
    public List<string> Skills { get; set; } = new();
    public List<string> Visas { get; set; } = new() { "Any" };
    public decimal? MinHourly { get; set; }
    public decimal? MaxHourly { get; set; }
    public DateTime? PostedUtc { get; set; }
    public DateTime FetchedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public int? RequirementId { get; set; }
    public bool IsHidden { get; set; }
}

/// <param name="IsDirect">True when the link goes to the employer's own careers site rather than a job board.</param>
public sealed record ApplyOption(string Publisher, string Url, bool IsDirect);

/// <summary>One call (or cache hit) to the job-search API. Drives the daily call limit and result caching.</summary>
public class ExternalJobSearch
{
    public int SearchId { get; set; }
    public string Provider { get; set; } = "JSearch";
    public string QueryKey { get; set; } = "";
    public string Query { get; set; } = "";
    public string? Location { get; set; }
    public int? UserId { get; set; }
    public bool Automatic { get; set; }
    public bool CacheHit { get; set; }
    public bool Succeeded { get; set; }
    public int ResultCount { get; set; }
    public List<int> JobIds { get; set; } = new();
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A vendor and the email domains / addresses its people write from. Only mail matching these is tracked.</summary>
public class Vendor
{
    public int VendorId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lower-case domains ("teksystems.com") or full addresses ("raj.k@gmail.com").</summary>
    public List<string> Domains { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A user's linked Outlook mailbox. The refresh token is encrypted with ASP.NET Data Protection.</summary>
public class MailConnection
{
    public int UserId { get; set; }
    public string Provider { get; set; } = "Microsoft";
    public string Mailbox { get; set; } = "";
    public string RefreshTokenProtected { get; set; } = "";
    /// <summary>Permissions Microsoft granted (space separated), e.g. whether Mail.Send was consented.</summary>
    public string? GrantedScopes { get; set; }
    /// <summary>Active | ReconnectNeeded</summary>
    public string Status { get; set; } = "Active";
    public string? LastError { get; set; }
    public DateTime ConnectedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncUtc { get; set; }
    /// <summary>Messages received up to this time have been read. Null = start from FirstSyncDays ago.</summary>
    public DateTime? SyncedThroughUtc { get; set; }
}

/// <summary>Summary of one email between a user and a vendor (no full body is stored).</summary>
public class VendorEmail
{
    public int VendorEmailId { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;
    public string MessageId { get; set; } = "";
    public string? ConversationId { get; set; }
    /// <summary>In = from the vendor, Out = to the vendor.</summary>
    public string Direction { get; set; } = "In";
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    public string ToAddresses { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Preview { get; set; } = "";
    public List<string> Attachments { get; set; } = new();
    public DateTime SentUtc { get; set; }
    public int? ConsultantId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>What the vendor's reply means (Autopilot): interview, rejected, needs_info, rate, closed, other. Null = not read yet.</summary>
    public string? Intent { get; set; }
    public string? IntentSummary { get; set; }
    /// <summary>The submission this reply is most likely about.</summary>
    public int? SubmissionId { get; set; }
    /// <summary>A recruiter acted on (or dismissed) the suggested next step.</summary>
    public bool IntentHandled { get; set; }
}

/// <summary>A reusable vendor email with {{merge.fields}}. Shared templates are visible to everyone; personal ones only to their owner.</summary>
public class EmailTemplate
{
    public int TemplateId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Other";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsShared { get; set; }
    /// <summary>Null for the built-in starter templates.</summary>
    public int? OwnerUserId { get; set; }
    public User? Owner { get; set; }
    public int UseCount { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>"Follow up with the vendor" reminder on a tracked email thread. Closes itself when the thread moves on.</summary>
public class FollowUpReminder
{
    public int ReminderId { get; set; }
    public int UserId { get; set; }
    /// <summary>The email that is waiting for an answer (the last one in the thread when the reminder was set).</summary>
    public int VendorEmailId { get; set; }
    public VendorEmail VendorEmail { get; set; } = null!;
    /// <summary>Outlook conversation id, or "#emailId" when Outlook didn't give one.</summary>
    public string ConversationKey { get; set; } = "";
    public DateTime DueUtc { get; set; }
    public string? Note { get; set; }
    /// <summary>Open | Done (you followed up) | Replied (the vendor answered) | Dismissed</summary>
    public string Status { get; set; } = "Open";
    /// <summary>Created by the app because the email got no reply in time (vs. set by the user).</summary>
    public bool Auto { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }
}

/// <summary>A version of a consultant's resume tailored for one job. The document is stored as JSON (Models.ResumeDoc).</summary>
public class TailoredResume
{
    public int TailoredResumeId { get; set; }
    public int ConsultantId { get; set; }
    public int? RequirementId { get; set; }
    /// <summary>The original resume it was built from.</summary>
    public int? SourceResumeId { get; set; }
    public string Name { get; set; } = "";
    public string TargetTitle { get; set; } = "";
    public string? TargetClient { get; set; }
    public string? JobDescription { get; set; }
    public string DocJson { get; set; } = "{}";
    public List<string> Changes { get; set; } = new();
    public List<string> Gaps { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Matched { get; set; } = new();
    /// <summary>claude | rules</summary>
    public string Engine { get; set; } = "rules";
    public string? Notice { get; set; }
    public bool HideContact { get; set; } = true;
    public int CreatedByUserId { get; set; }
    public User? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One opportunity the Autopilot found for a consultant (an internal requirement or an external job) and its progress:
/// Found → Prepared → (recruiter approves) RtrSent → (consultant approves) RtrApproved → Submitted.
/// Side exits: NeedsResume, Skipped, RtrDeclined, RtrExpired, Failed.
/// </summary>
public class AutopilotItem
{
    public int AutopilotItemId { get; set; }
    public int ConsultantId { get; set; }
    /// <summary>The consultant's recruiter (owner) when the item was found.</summary>
    public int OwnerUserId { get; set; }
    public int? RequirementId { get; set; }
    public int? ExternalJobId { get; set; }
    public string JobTitle { get; set; } = "";
    public string? Client { get; set; }
    public string? Vendor { get; set; }
    public int Score { get; set; }
    public string? Why { get; set; }
    public string Status { get; set; } = "Found";
    public int? TailoredResumeId { get; set; }
    public decimal? BillRate { get; set; }
    public decimal? SubmitRate { get; set; }
    public decimal? PayRate { get; set; }
    public string? ContractType { get; set; }
    public string? VendorEmail { get; set; }
    public string? EmailSubject { get; set; }
    public string? EmailBody { get; set; }
    public string? Error { get; set; }
    public int? ApprovedByUserId { get; set; }
    public int? SubmissionId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedUtc { get; set; }
    public DateTime? SubmittedUtc { get; set; }
}

/// <summary>
/// The consultant's Right to Represent, collected through a one-time link. Only a SHA-256 hash of the token is stored.
/// The exact statement they approved, the time, IP and browser are kept as evidence.
/// </summary>
public class RtrRequest
{
    public int RtrRequestId { get; set; }
    public int AutopilotItemId { get; set; }
    public string TokenHash { get; set; } = "";
    public string ConsultantEmail { get; set; } = "";
    /// <summary>Pending | Approved | Declined | Expired</summary>
    public string Status { get; set; } = "Pending";
    public string Statement { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedUtc { get; set; }
    public string? SignedName { get; set; }
    public string? DeclineReason { get; set; }
    public string? RespondedIp { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>Company-wide Autopilot settings (a single row, id 1), changed by Team Lead and above.</summary>
public class AutopilotSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public int MinScore { get; set; } = 70;
    public int MaxNewPerConsultant { get; set; } = 3;
    public int MaxPreparePerRun { get; set; } = 20;
    public bool IncludeExternalJobs { get; set; } = true;
    public bool AutoFollowUp { get; set; } = true;
    public int MaxAutoFollowUps { get; set; } = 2;
    public bool ClassifyReplies { get; set; } = true;
    public int RunEveryMinutes { get; set; } = 60;
    /// <summary>Email recruiters about Autopilot events (each can still opt out of categories).</summary>
    public bool EmailNotifications { get; set; } = true;
    public DateTime? LastRunUtc { get; set; }
    public string? LastRunSummary { get; set; }
}

/// <summary>An Autopilot event queued for a recruiter's email digest. Sent in batches: one email per recipient per send.</summary>
public class AutopilotNotification
{
    public long NotificationId { get; set; }
    public int UserId { get; set; }
    /// <summary>approval | reply | rtr | problem | followup (matches the recipient's preferences)</summary>
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>App path to open, e.g. /autopilot?tab=replies</summary>
    public string Link { get; set; } = "/autopilot";
    public int? AutopilotItemId { get; set; }
    /// <summary>Pending | Sent | Skipped | Failed</summary>
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentUtc { get; set; }
}

/// <summary>A user's Autopilot email preferences. No row = the defaults below.</summary>
public class NotificationPrefs
{
    public int UserId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Approvals { get; set; } = true;
    public bool VendorReplies { get; set; } = true;
    public bool RtrUpdates { get; set; } = true;
    public bool Problems { get; set; } = true;
    public bool FollowUps { get; set; }
}

/// <summary>A candidate (usually captured from LinkedIn) shortlisted or rejected for one requirement, before any submission.</summary>
public class CandidateLead
{
    public int LeadId { get; set; }
    public int ConsultantId { get; set; }
    public Consultant Consultant { get; set; } = null!;
    public int RequirementId { get; set; }
    public Requirement Requirement { get; set; } = null!;
    /// <summary>Mapped (shortlisted for the job) | Rejected (not a fit or not interested)</summary>
    public string Status { get; set; } = "Mapped";
    public string? Reason { get; set; }
    public int UpdatedByUserId { get; set; }
    public User UpdatedBy { get; set; } = null!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One outreach touch with a candidate: a LinkedIn message, call, email, SMS or note, with its outcome.</summary>
public class CandidateActivity
{
    public int ActivityId { get; set; }
    public int ConsultantId { get; set; }
    public Consultant Consultant { get; set; } = null!;
    public int? RequirementId { get; set; }
    public Requirement? Requirement { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Channel { get; set; } = "Note";
    public string? Outcome { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A reminder to get back to a candidate (e.g. "call back Thursday").</summary>
public class CandidateReminder
{
    public int ReminderId { get; set; }
    public int ConsultantId { get; set; }
    public Consultant Consultant { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime DueUtc { get; set; }
    public string? Note { get; set; }
    /// <summary>Open | Done | Dismissed</summary>
    public string Status { get; set; } = "Open";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }
}
