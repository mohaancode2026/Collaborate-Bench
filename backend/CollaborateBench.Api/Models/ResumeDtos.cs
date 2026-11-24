// Request/response types for the AI resume builder (ResumesController).
namespace CollaborateBench.Api.Models;

/// <summary>A structured resume: what the editor shows and what the Word export renders.</summary>
public sealed class ResumeDoc
{
    public string Name { get; set; } = "";
    /// <summary>Professional headline, e.g. "Senior Java Developer". Must reflect the consultant's real title.</summary>
    public string Headline { get; set; } = "";
    /// <summary>Contact details. Empty when the version hides contact details (common for vendor submissions).</summary>
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public string Summary { get; set; } = "";
    public List<SkillGroup> Skills { get; set; } = new();
    public List<ResumeJob> Experience { get; set; } = new();
    public List<ResumeEducation> Education { get; set; } = new();
    public List<string> Certifications { get; set; } = new();
    /// <summary>Original text kept as-is when it couldn't be structured (built-in engine).</summary>
    public string? AdditionalText { get; set; }
    /// <summary>The whole document is the original resume text (no "Professional Experience" heading over it). Export only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WholeTextOnly { get; set; }

    // ----- design (preview, print and Word export all follow these) -----
    /// <summary>classic | modern | compact</summary>
    public string Template { get; set; } = "classic";
    /// <summary>Accent colour as 6 hex digits, e.g. "1E293B".</summary>
    public string Accent { get; set; } = "1E293B";
    /// <summary>Sections left out of this version: summary, skills, experience, education, certifications.</summary>
    public List<string> HiddenSections { get; set; } = new();

    public static readonly string[] Templates = ["classic", "modern", "compact"];
    public static readonly string[] Sections = ["summary", "skills", "experience", "education", "certifications"];
    public bool Shows(string section) => !HiddenSections.Contains(section, StringComparer.OrdinalIgnoreCase);
}

public sealed class SkillGroup
{
    public string Category { get; set; } = "";
    public List<string> Items { get; set; } = new();
}

public sealed class ResumeJob
{
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Location { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
    public List<string> Bullets { get; set; } = new();
    /// <summary>Tools and technologies used in this role, as listed in the original resume.</summary>
    public string? Environment { get; set; }
}

public sealed class ResumeEducation
{
    public string Degree { get; set; } = "";
    public string? School { get; set; }
    public string? Year { get; set; }
}

/// <summary>The consultant's original resume text (the only source of facts for tailoring).</summary>
public sealed record ResumeSourceDto(int ResumeId, string? FileName, string Text, DateTime UploadedUtc, string? UploadedBy);
public sealed record ResumeSourceInput(int ConsultantId, string? Text, string? FileName);

/// <summary>Tailor for a requirement in the system, or for a pasted job description (JobTitle + JobDescription).</summary>
/// <param name="HideContact">Leave email and phone off the resume (vendors get them only after the RTR). Default true.</param>
/// <param name="Focus">Optional instruction, e.g. "emphasise banking domain and Kafka".</param>
public sealed record TailorInput(int ConsultantId, int? RequirementId, string? JobTitle, string? Client, string? JobDescription,
    bool HideContact = true, string? Focus = null);

/// <param name="Changes">What was changed compared to the original, for the recruiter to review.</param>
/// <param name="Gaps">Skills the job asks for that the original resume doesn't show. Never added to the resume.</param>
/// <param name="Warnings">Content that couldn't be verified against the original resume. Check before sending.</param>
/// <param name="Matched">Job skills the original resume does show.</param>
public sealed record TailoredResumeDto(int Id, int ConsultantId, string ConsultantName, int? RequirementId, string Name, string TargetTitle,
    string? TargetClient, ResumeDoc Doc, List<string> Changes, List<string> Gaps, List<string> Warnings, List<string> Matched,
    string Engine, string? Notice, bool HideContact, string? CreatedBy, DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed record TailoredResumeUpdate(string? Name, ResumeDoc? Doc);
