// Request/response types for the bench sales Autopilot and the consultant RTR page.
namespace CollaborateBench.Api.Models;

/// <summary>The recruiter's final values when approving: they are what the consultant sees in the RTR and what the vendor receives.</summary>
/// <param name="BillRate">Required for external jobs (job boards rarely show it). Ignored for internal requirements.</param>
/// <param name="ContractType">C2C, W2 or 1099. Required for external jobs.</param>
public sealed record AutopilotApproveInput(string? VendorEmail, string? EmailSubject, string? EmailBody, decimal? PayRate, decimal? SubmitRate,
    decimal? BillRate, string? ContractType);

public sealed record RtrInfo(string Status, DateTime CreatedUtc, DateTime ExpiresUtc, DateTime? RespondedUtc, string? SignedName, string? DeclineReason);

public sealed record AutopilotItemDto(int Id, string Status, string StatusLabel, int ConsultantId, string ConsultantName, string ConsultantTitle,
    string? ConsultantEmail, int OwnerId, string OwnerName, int? RequirementId, int? ExternalJobId, string JobTitle, string? Client, string? Vendor,
    int Score, string? Why, int? TailoredResumeId, int ResumeWarnings, List<string> Gaps, decimal? BillRate, decimal? SubmitRate, decimal? PayRate,
    string? ContractType, string? VendorEmail, string? EmailSubject, string? EmailBody, string? Error, string? ApprovedBy, DateTime? ApprovedUtc,
    int? SubmissionId, DateTime? SubmittedUtc, RtrInfo? Rtr, DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed record AutopilotSummary(bool Enabled, DateTime? LastRunUtc, string? LastRunSummary, int Found, int NeedsResume, int AwaitingApproval,
    int AwaitingRtr, int SubmittedThisWeek, int RepliesToReview, int Failed);

public sealed record AutopilotSettingsDto(bool Enabled, int MinScore, int MaxNewPerConsultant, int MaxPreparePerRun, bool IncludeExternalJobs,
    bool AutoFollowUp, int MaxAutoFollowUps, bool ClassifyReplies, int RunEveryMinutes, DateTime? LastRunUtc, string? LastRunSummary,
    bool AiConfigured, bool CanManage, bool EmailNotifications);

public sealed record AutopilotSettingsInput(bool Enabled, int MinScore, int MaxNewPerConsultant, int MaxPreparePerRun, bool IncludeExternalJobs,
    bool AutoFollowUp, int MaxAutoFollowUps, bool ClassifyReplies, int RunEveryMinutes, bool EmailNotifications = true);

/// <summary>A user's Autopilot email alert choices, plus how alerts reach them.</summary>
/// <param name="SendsFrom">The mailbox alerts come from, or null when they're sent from the user's own Outlook.</param>
/// <param name="CanReceive">False when alerts can't be sent (company switch off, or no Outlook connected to send from).</param>
public sealed record NotificationPrefsDto(bool Enabled, bool Approvals, bool VendorReplies, bool RtrUpdates, bool Problems, bool FollowUps,
    string? SendsFrom, bool CanReceive, string? Reason, DateTime? LastSentUtc);
public sealed record NotificationPrefsInput(bool Enabled, bool Approvals, bool VendorReplies, bool RtrUpdates, bool Problems, bool FollowUps);

/// <summary>A vendor reply the Autopilot read, with what it means and the submission it is about.</summary>
public sealed record VendorReplyDto(int EmailId, DateTime SentUtc, string Vendor, string FromAddress, string? FromName, string Subject, string Preview,
    string Intent, string? Summary, int? SubmissionId, int? ConsultantId, string? ConsultantName, string? JobTitle, string? Stage, int UserId, string UserName);

/// <summary>What the consultant sees on the RTR page. Deliberately minimal: no bill or submit rate, no other consultants.</summary>
public sealed record RtrView(string Status, string ConsultantFirstName, string JobTitle, string? Client, string? Vendor, string? Location,
    string? ContractType, decimal? PayRate, string Statement, string RecruiterName, string CompanyName, DateTime ExpiresUtc, bool Expired);

/// <param name="Decision">"approve" or "decline".</param>
/// <param name="SignedName">The consultant's typed full name (required to approve).</param>
public sealed record RtrResponseInput(string? Decision, string? SignedName, string? Reason);
