namespace CollaborateBench.Api.Models;

public static class Domain
{
    public static readonly string[] Visas = ["USC", "GC", "GC-EAD", "H1B", "H4-EAD", "L2-EAD", "OPT", "CPT", "STEM OPT", "TN", "EAD"];
    /// <summary>"Lead" = sourced (e.g. from LinkedIn) but not yet on the bench; leads stay out of bench lists, matching and the dashboard.</summary>
    public static readonly string[] ConsultantStatuses = ["Bench", "Marketing", "Placed", "Lead"];
    public static readonly string[] Stages = ["Submitted", "Vendor Screen", "Client Interview", "Offer", "MSA / PO", "Billing"];
    public static readonly string[] WorkModes = ["Remote", "Hybrid", "Onsite"];
    public static readonly string[] ContractTypes = ["C2C", "W2", "1099"];
    public static readonly string[] Tiers = ["Direct", "Prime vendor", "Implementation partner", "Tier-1", "Tier-2"];
    public static readonly string[] Channels = ["Dice", "LinkedIn Jobs", "Indeed", "Monster", "C2C Portals", "Vendor Email", "VMS Portal", "Corp Website"];
    public static readonly (string Code, string Label)[] OnboardingItems =
    [
        ("MSA", "Signed MSA with vendor"), ("PO", "Purchase Order received"), ("RTR_OFFER", "RTR & offer letter on file"),
        ("I9", "I-9 / work authorization verified"), ("BGV", "Background check cleared"), ("DRUG", "Drug screen cleared"),
        ("ACCESS", "Client system access & laptop"), ("TIMESHEET", "First timesheet approved"), ("INVOICE", "First invoice sent"),
    ];
}

// ---------- Auth & users ----------
public sealed record LoginRequest(string? Email, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record UserInfo(int Id, string Name, string Email, string Role, string DataScope, string? Team,
    string? ManagerName, IReadOnlyList<string> Permissions, bool MustChangePassword);
public sealed record LoginResponse(string Token, DateTime ExpiresUtc, UserInfo User);
/// <param name="Mode">"login" (sign in) or "connect" (link Outlook to the signed-in user).</param>
public sealed record MsStartRequest(string? Mode, string? LoginHint);
/// <param name="Code">One-time code from the Microsoft callback redirect.</param>
public sealed record MsExchangeRequest(string? Code);
/// <param name="MailConnected">False when Microsoft returned no refresh token, so Outlook mail tracking isn't connected.</param>
/// <param name="NewAccount">True when this sign-in created the account (first Microsoft sign-in, Microsoft:AutoProvisionRole).</param>
public sealed record MsLoginResponse(string Token, DateTime ExpiresUtc, UserInfo User, bool MailConnected, bool NewAccount);

public sealed class TeamMemberDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
    public int? ManagerId { get; set; }
    public string? ManagerName { get; set; }
    public string? Team { get; set; }
    public bool Active { get; set; }
    public bool Locked { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public int Consultants { get; set; }
    public int ActiveSubmissions { get; set; }
    public int Placed { get; set; }
}
public sealed record UserInput(string? Name, string? Email, string? Role, int? ManagerId, string? Team, bool? Active);

// ---------- Consultants ----------
public sealed class ConsultantDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Visa { get; set; } = "";
    public string Location { get; set; } = "";
    public bool Relocate { get; set; }
    public decimal Rate { get; set; }
    public int Exp { get; set; }
    public string Status { get; set; } = "";
    public List<string> Skills { get; set; } = new();
    public string Summary { get; set; } = "";
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public string? OwnerTeam { get; set; }
    public int DaysOnBench { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime CreatedUtc { get; set; }
    public int ActiveSubmissions { get; set; }
    public string? LinkedInUrl { get; set; }
    public string Source { get; set; } = "Manual";
    /// <summary>Masked passport number (••••1234) when one is on file; the full number only via GET consultants/{id}/passport.</summary>
    public string? PassportOnFile { get; set; }
    public DateTime? VisaValidUntil { get; set; }
    public string? Availability { get; set; }
    public string? PreferredLocations { get; set; }
    public string? Education { get; set; }
    public string? Certifications { get; set; }
    public int? UsExp { get; set; }
    public string RowVersion { get; set; } = "";
}
public sealed record ConsultantInput(string? Name, string? Title, string? Email, string? Phone, string? Visa, string? Location,
    bool Relocate, decimal Rate, int Exp, string? Status, List<string>? Skills, string? Summary, int? OwnerId,
    string? RowVersion, string? ResumeText, string? ResumeFileName, string? ParsedBy,
    string? LinkedInUrl = null,
    /// <summary>New passport number; leave empty to keep the one on file.</summary>
    string? PassportNumber = null,
    /// <summary>True removes the passport number on file.</summary>
    bool ClearPassport = false,
    DateTime? VisaValidUntil = null, string? Availability = null, string? PreferredLocations = null,
    string? Education = null, string? Certifications = null, int? UsExp = null);

// ---------- Requirements ----------
public sealed class RequirementDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Client { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Location { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Type { get; set; } = "";
    public decimal Rate { get; set; }
    public List<string> Skills { get; set; } = new();
    public List<string> Visas { get; set; } = new();
    public string? Description { get; set; }
    public string Source { get; set; } = "";
    public string Status { get; set; } = "";
    public int PostedById { get; set; }
    public string PostedByName { get; set; } = "";
    public DateTime PostedUtc { get; set; }
    public int DaysOld { get; set; }
    public bool Saved { get; set; }
    public int ActiveSubmissions { get; set; }
}
public sealed record RequirementInput(string? Title, string? Client, string? Vendor, string? Tier, string? Location, string? Mode,
    string? Type, decimal Rate, List<string>? Skills, List<string>? Visas, string? Description, string? Source, List<string>? Channels);
public sealed record StatusInput(string? Status);

// ---------- Submissions ----------
public sealed class SubmissionDto
{
    public int Id { get; set; }
    public int ConsultantId { get; set; }
    public string ConsultantName { get; set; } = "";
    public string ConsultantTitle { get; set; } = "";
    public int RequirementId { get; set; }
    public string Title { get; set; } = "";
    public string Client { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Type { get; set; } = "";
    public decimal BillRate { get; set; }
    public string Stage { get; set; } = "";
    public decimal SubmitRate { get; set; }
    public decimal PayRate { get; set; }
    /// <summary>Only filled for roles with margin.view</summary>
    public decimal? MarginPerHour { get; set; }
    public int? MatchScore { get; set; }
    public bool Rtr { get; set; }
    public string? Notes { get; set; }
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public int DaysInStage { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public DateTime StageChangedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string RowVersion { get; set; } = "";
}
public sealed record SubmissionInput(int ConsultantId, int RequirementId, decimal SubmitRate, decimal PayRate, bool RtrSigned, string? Notes);
public sealed record StageInput(string? ToStage, string? RowVersion);
public sealed record OnboardingItemDto(string Code, string Label, bool Done, string? DoneBy, DateTime? DoneUtc);
public sealed record OnboardingInput(bool Done);

// ---------- Matching ----------
public sealed record MatchResult(int ConsultantId, string ConsultantName, string ConsultantTitle, string Visa, decimal PayRate, string OwnerName,
    int RequirementId, string RequirementTitle, string Client, string Vendor, string Type, decimal BillRate,
    int Score, List<string> Hit, List<string> Miss, string Why, bool VisaOk, string? SubmittedStage);

// ---------- Dashboard ----------
public sealed record Insight(string Type, string Title, string Detail, string ActionLabel, string ActionPath);
public sealed record StageCount(string Stage, int Count);
public sealed record ActivityDto(long Id, string UserName, string Role, string Action, string Detail, string Outcome, DateTime CreatedUtc);
public sealed record DashboardDto(int Bench, int Marketing, int OpenRequirements, int RequirementsToday, int ActiveSubmissions,
    int InInterviewOrOffer, int Billing, decimal? WeeklyMargin, List<StageCount> Stages, List<Insight> Insights,
    List<MatchResult> TopMatches, List<ActivityDto> Activity, List<RecruiterStat> ByRecruiter);

/// <summary>One row of the recruiter-wise report. Bench is a current count; every submission figure covers
/// submissions created in the requested date range (all time when none), withdrawn excluded.</summary>
public sealed record RecruiterStat(int UserId, string Name, string Role, string? Team, int Bench, int Marketing,
    List<StageCount> Stages, int Total, int Active, int Placed, int Stale, decimal? WeeklyMargin, DateTime? LastSubmissionUtc);

// ---------- AI ----------
public sealed record AiText(string Text, string Source, string? Notice = null);
public sealed record ResumeTextInput(string? Text, string? FileName);
public sealed class ParsedResume
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Location { get; set; } = "";
    public string Visa { get; set; } = "";
    public int Exp { get; set; }
    public decimal? Rate { get; set; }
    public List<string> Skills { get; set; } = new();
    public string Summary { get; set; } = "";
    public bool Relocate { get; set; }
    public string LinkedInUrl { get; set; } = "";
    public string Passport { get; set; } = "";
    public string Education { get; set; } = "";
    public string Certifications { get; set; } = "";
    public string Availability { get; set; } = "";
    public string Source { get; set; } = "rules";
    public string? Notice { get; set; }
    public string? DuplicateWarning { get; set; }
    public List<MatchResult> TopMatches { get; set; } = new();
}
public sealed record JdInput(string? Title, string? Client, string? Vendor, string? Tier, string? Location, string? Mode, string? Type, decimal Rate, List<string>? Skills, List<string>? Visas);
public sealed record TextInput(string? Text);
public sealed record PairInput(int ConsultantId, int RequirementId);
public sealed record RateAdvice(decimal Rate, string Why, string Source, string? Notice = null);
public sealed record ExtractedRequirement(RequirementInput Requirement, string Source, string? Notice = null);
public sealed record ChatMessage(string? Role, string? Content);
public sealed record CopilotInput(List<ChatMessage>? Messages);

// ---------- External jobs (third-party job-search API) ----------
public sealed class ExternalJobDto
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    public string? Publisher { get; set; }
    public string Title { get; set; } = "";
    public string Company { get; set; } = "";
    public string Location { get; set; } = "";
    public bool Remote { get; set; }
    public string Mode { get; set; } = "";
    public string? EmploymentType { get; set; }
    /// <summary>C2C / W2 / 1099 guessed from the description; the user confirms it when importing.</summary>
    public string SuggestedType { get; set; } = "C2C";
    public decimal? MinRate { get; set; }
    public decimal? MaxRate { get; set; }
    public List<string> Skills { get; set; } = new();
    public List<string> Visas { get; set; } = new();
    public string? Description { get; set; }
    public string ApplyUrl { get; set; } = "";
    public List<CollaborateBench.Api.Data.ApplyOption> ApplyOptions { get; set; } = new();
    public bool OnLinkedIn { get; set; }
    public List<string> ContactEmails { get; set; } = new();
    public DateTime? PostedUtc { get; set; }
    public int? DaysOld { get; set; }
    public DateTime FetchedUtc { get; set; }
    public int? RequirementId { get; set; }
    public bool Hidden { get; set; }
    public int TopScore { get; set; }
    /// <summary>Best consultants in the user's view (top 3), or the chosen consultant's score.</summary>
    public List<MatchResult> Matches { get; set; } = new();
}
/// <param name="Skills">Skills to search for (e.g. the ones picked from the consultant's profile). Joined into the query.</param>
public sealed record ExternalSearchInput(string? Query, string? Location, int? ConsultantId, bool RemoteOnly, string? DatePosted, bool ContractOnly = true,
    List<string>? Skills = null, bool IncludeTitle = false);
public sealed record ExternalSearchResult(int SearchId, string Query, string? Location, bool Cached, int Found, int New, List<ExternalJobDto> Jobs);
public sealed record ExternalImportInput(decimal BillRate, string? Type, string? Vendor, string? Tier, List<string>? Skills, List<string>? Visas);
public sealed record ExternalApplyInput(int ConsultantId, decimal SubmitRate, decimal PayRate, bool RtrSigned, string? Notes,
    decimal BillRate, string? Type, string? Vendor, string? Tier, List<string>? Skills, List<string>? Visas);
/// <param name="Provider">The sources searched, for display ("Dice and JSearch (Indeed, LinkedIn, Glassdoor)").</param>
/// <param name="CallsToday">JSearch calls today, against <paramref name="DailyLimit"/>. Dice is free and not counted.</param>
public sealed record JobFeedStatus(bool Configured, string Provider, int CallsToday, int DailyLimit, int AutoRefreshHours, DateTime? LastSearchUtc, int StoredJobs,
    bool Dice = false, bool Indeed = false);
