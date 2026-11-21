using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>AI helpers. Each one uses Claude when configured and falls back to the built-in rules engine otherwise,
/// so the features keep working without an API key. Rate-limited per user ("ai" policy) to control cost.</summary>
[ApiController, Route("api/ai"), EnableRateLimiting("ai")]
public sealed class AiController(AiService ai, DataService data, AppDbContext db, ClaudeClient claude, AuditService audit) : ControllerBase
{
    /// <summary>Whether Claude is configured, so the UI can show which engine answers.</summary>
    [HttpGet("status")]
    public object Status() => new { claude = claude.Enabled, engine = claude.Enabled ? "Claude" : "Built-in" };

    /// <summary>Extracts name, contact, skills, visa, rate etc. from resume text, warns about likely duplicates and suggests the top matching jobs.</summary>
    [HttpPost("parse-resume"), RequirePerm("parser.use")]
    public async Task<ParsedResume> ParseResume(ResumeTextInput input, CancellationToken ct)
    {
        var text = (input.Text ?? "").Trim();
        new Validator()
            .Check(text.Length >= 40, "text", "Paste at least a few lines of resume text, or upload a file")
            .Check(text.Length <= 60000, "text", "That resume is too long (60,000 characters max)")
            .ThrowIfInvalid();
        var p = await ai.ParseResumeAsync(text, ct);

        if (!string.IsNullOrEmpty(p.Email))
        {
            // Row-level security limits this check to consultants you can see; the unique email index
            // still blocks a cross-team duplicate when the profile is saved.
            var email = p.Email.ToLowerInvariant();
            var dupOwner = await db.Consultants.Where(c => c.Email == email && c.Status != "Inactive").Select(c => c.Owner.FullName).FirstOrDefaultAsync(ct);
            if (dupOwner is string owner) p.DuplicateWarning = $"A consultant with {p.Email} is already on the bench (owner: {owner}).";
        }

        var probe = new ConsultantDto { Name = p.Name, Title = p.Title, Visa = p.Visa, Location = p.Location, Relocate = p.Relocate, Rate = p.Rate ?? 0, Exp = p.Exp, Skills = p.Skills };
        p.TopMatches = (await data.RequirementsAsync(true, ct)).Select(r => Matching.Score(probe, r)).OrderByDescending(m => m.Score).Take(3).ToList();
        await audit.LogAsync("AI_PARSE", $"Parsed a resume ({p.Source}){(string.IsNullOrEmpty(p.Name) ? "" : ": " + p.Name)}");
        return p;
    }

    /// <summary>Writes a job description from the requirement fields.</summary>
    [HttpPost("generate-jd"), RequirePerm("jobs.create")]
    public Task<AiText> GenerateJd(JdInput input, CancellationToken ct)
    {
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(input.Title), "title", "Job title is required")
            .Check(input.Skills is { Count: > 0 }, "skills", "Add at least one required skill")
            .ThrowIfInvalid();
        return ai.JobDescriptionAsync(input with { Client = string.IsNullOrWhiteSpace(input.Client) ? "Confidential client" : input.Client }, ct);
    }

    /// <summary>Turns a pasted vendor email or job post into requirement fields.</summary>
    [HttpPost("extract-requirement"), RequirePerm("jobs.create")]
    public Task<ExtractedRequirement> ExtractRequirement(TextInput input, CancellationToken ct)
    {
        var text = (input.Text ?? "").Trim();
        new Validator().Check(text.Length >= 30, "text", "Paste the requirement email first").Check(text.Length <= 20000, "text", "That email is too long").ThrowIfInvalid();
        return ai.ExtractRequirementAsync(text, ct);
    }

    /// <summary>Writes a submission email pitching the consultant for the requirement (both must be in your view).</summary>
    [HttpPost("pitch"), RequirePerm("sub.create")]
    public async Task<AiText> Pitch(PairInput input, CancellationToken ct) =>
        await ai.PitchAsync(await data.ConsultantAsync(input.ConsultantId, ct), await data.RequirementAsync(input.RequirementId, ct), ct);

    /// <summary>Suggests a submit rate between the consultant's pay rate and the bill rate, with the reasoning.</summary>
    [HttpPost("rate-advice"), RequirePerm("sub.create")]
    public async Task<RateAdvice> RateAdvice(PairInput input, CancellationToken ct) =>
        await ai.RateAdviceAsync(await data.ConsultantAsync(input.ConsultantId, ct), await data.RequirementAsync(input.RequirementId, ct), ct);

    /// <summary>Interview preparation notes for a submission: likely questions, skill gaps and talking points.</summary>
    [HttpPost("interview-prep/{submissionId:int}"), RequirePerm("pipeline.view")]
    public async Task<AiText> InterviewPrep(int submissionId, CancellationToken ct)
    {
        var s = await data.SubmissionAsync(submissionId, ct);
        var result = await ai.InterviewPrepAsync(s, await data.ConsultantAsync(s.ConsultantId, ct), await data.RequirementAsync(s.RequirementId, ct), ct);
        await audit.LogAsync("AI_PREP", $"Generated interview prep for {s.ConsultantName}", "Submission", submissionId);
        return result;
    }

    /// <summary>Short daily briefing for the dashboard: bench, risks and the biggest opportunity in your view.</summary>
    [HttpPost("briefing"), RequirePerm("dash.view")]
    public Task<AiText> Briefing([FromQuery] string? scope, CancellationToken ct) => ai.BriefingAsync(scope, ct);

    /// <summary>Chat assistant that answers questions using your data (read-only, limited to your view).</summary>
    [HttpPost("copilot"), RequirePerm("ai.copilot")]
    public Task<AiText> Copilot(CopilotInput input, CancellationToken ct) => ai.CopilotAsync(input.Messages ?? new(), ct);
}
