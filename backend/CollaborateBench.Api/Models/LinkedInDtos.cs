namespace CollaborateBench.Api.Models;

// ---------- LinkedIn side panel (browser extension) ----------
// The extension reads only the LinkedIn page the recruiter has open and sends what is on screen; nothing is crawled.

/// <summary>What the extension read from a LinkedIn profile page.</summary>
public sealed record LiProfileInput(string? Url, string? Name, string? Headline, string? Location, string? About,
    string? Experience, string? Education, List<string>? Skills, string? Email, string? Phone);

/// <summary>A heads-up for the recruiter. Level: info | warn | danger.</summary>
public sealed record LiInsight(string Level, string Text);

/// <summary>Set when the profile is already saved by someone outside your team (you can't open it).</summary>
public sealed record LiOwnerHint(string OwnerName, string? Team);

/// <summary>A requirement this candidate is linked to: mapped or rejected as a lead, or submitted.</summary>
/// <param name="Status">Mapped | Rejected | Submitted</param>
public sealed record LeadDto(int? LeadId, int RequirementId, string RequirementTitle, string Client, string Status, string? Reason,
    string? Stage, int? SubmissionId, string ByName, DateTime UpdatedUtc, string? LastActivity, DateTime? LastActivityUtc, int ActivityCount);

/// <param name="Kind">activity (a logged touch) | lead (mapped/rejected) | submission</param>
public sealed record CandidateActivityDto(int Id, string Kind, int? RequirementId, string? RequirementTitle, string Channel, string? Outcome,
    string? Note, string ByName, DateTime CreatedUtc);

public sealed record CandidateReminderDto(int Id, int ConsultantId, string ConsultantName, string? LinkedInUrl, DateTime DueUtc, string? Note,
    string Status, bool Overdue, string ByName, bool Mine);

/// <summary>Everything the side panel shows for one profile.</summary>
/// <param name="Found">The profile is saved and you can see it (Consultant is set).</param>
public sealed record LiProfileContext(bool Found, string? LinkedInUrl, ConsultantDto? Consultant, LiOwnerHint? SavedElsewhere,
    List<LiInsight> Insights, List<LeadDto> Leads, List<CandidateActivityDto> Activity, List<CandidateReminderDto> Reminders,
    List<MatchResult> Suggestions, string? Engine = null, string? Notice = null);

/// <param name="Status">Mapped | Rejected</param>
public sealed record LeadInput(int ConsultantId, int RequirementId, string? Status, string? Reason);

/// <param name="Channel">See LinkedInController.Channels.</param>
/// <param name="Outcome">See LinkedInController.Outcomes (optional).</param>
/// <param name="RemindUtc">Optional: also set a reminder for this time.</param>
public sealed record CandidateActivityInput(int ConsultantId, int? RequirementId, string? Channel, string? Outcome, string? Note, DateTime? RemindUtc);

public sealed record CandidateReminderInput(int ConsultantId, DateTime DueUtc, string? Note);
public sealed record CandidateReminderStatusInput(string? Status);

/// <summary>A sourced lead for the portal's Leads page.</summary>
public sealed record LeadRow(ConsultantDto Consultant, string? LastActivity, DateTime? LastActivityUtc, string? LastActivityBy,
    DateTime? NextReminderUtc, int Mapped, int Rejected);

/// <summary>What the extension read from a LinkedIn job post.</summary>
public sealed record LiJobInput(string? Url, string? Title, string? Company, string? Location, string? Workplace, string? EmploymentType, string? Description);

/// <summary>The side panel's view of a job post: the saved requirement, or an AI-extracted draft to review and save, plus bench matches.</summary>
public sealed record LiJobContext(bool Found, string? SourceUrl, RequirementDto? Requirement, RequirementInput? Draft, string? Engine, string? Notice,
    List<MatchResult> Matches, List<LiInsight> Insights);

public sealed record LiJobSaveInput(string? Url, RequirementInput? Requirement);
