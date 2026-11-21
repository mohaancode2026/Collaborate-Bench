using System.Data;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>Requirements (jobs) from vendors and clients. Shared across teams; posting and closing need their own permissions.</summary>
[ApiController, Route("api/requirements")]
public sealed class RequirementsController(AppDbContext db, DataService data, ICurrentUser me, AuditService audit) : ControllerBase
{
    /// <summary>Requirements with job-board filters: keyword, location, work mode, contract type, visa, minimum rate, age and saved.</summary>
    [HttpGet, RequirePerm("jobs.view")]
    public async Task<List<RequirementDto>> List([FromQuery] string? status, [FromQuery] string? q, [FromQuery] string? location, [FromQuery] string? mode,
        [FromQuery] string? type, [FromQuery] string? visa, [FromQuery] decimal? minRate, [FromQuery] int? postedDays, [FromQuery] bool savedOnly = false)
    {
        IEnumerable<RequirementDto> r = await data.RequirementsAsync(status == "Open");
        if (!string.IsNullOrWhiteSpace(q)) r = r.Where(j => $"{j.Title} {j.Client} {j.Vendor} {string.Join(' ', j.Skills)}".Contains(q, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(location)) r = r.Where(j => j.Location.Contains(location, StringComparison.OrdinalIgnoreCase) || (location.Contains("remote", StringComparison.OrdinalIgnoreCase) && j.Mode == "Remote"));
        if (!string.IsNullOrWhiteSpace(mode)) { var m = mode.Split(','); r = r.Where(j => m.Contains(j.Mode)); }
        if (!string.IsNullOrWhiteSpace(type)) { var t = type.Split(','); r = r.Where(j => t.Contains(j.Type)); }
        if (!string.IsNullOrWhiteSpace(visa)) r = r.Where(j => j.Visas.Contains("Any") || j.Visas.Contains(visa));
        if (minRate is > 0) r = r.Where(j => j.Rate >= minRate);
        if (postedDays is >= 0) r = r.Where(j => j.DaysOld <= postedDays);
        if (savedOnly) r = r.Where(j => j.Saved);
        return r.ToList();
    }

    /// <summary>One requirement.</summary>
    [HttpGet("{id:int}"), RequirePerm("jobs.view")]
    public Task<RequirementDto> Get(int id) => data.RequirementAsync(id);

    /// <summary>Posts a requirement and optionally queues it for job boards. A likely duplicate (same title and client still open)
    /// returns 409 DUPLICATE_REQUIREMENT unless <paramref name="force"/> is true.</summary>
    [HttpPost, RequirePerm("jobs.create")]
    public async Task<RequirementDto> Create(RequirementInput j, [FromQuery] bool force = false)
    {
        var skills = (j.Skills ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var channels = (j.Channels ?? new()).Where(Domain.Channels.Contains).Distinct().ToList();
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(j.Title), "title", "Job title is required")
            .Check(!string.IsNullOrWhiteSpace(j.Client), "client", "End client is required (use \"Confidential\" if needed)")
            .Check(j.Rate is >= 10 and <= 400, "rate", "Enter a bill rate between $10 and $400/hr")
            .Check(skills.Count > 0, "skills", "Add at least one required skill")
            .Check(Domain.WorkModes.Contains(j.Mode), "mode", "Choose Remote, Hybrid or Onsite")
            .Check(Domain.ContractTypes.Contains(j.Type), "type", "Choose C2C, W2 or 1099")
            .Check(string.IsNullOrEmpty(j.Tier) || Domain.Tiers.Contains(j.Tier), "tier", "Choose a valid vendor tier")
            .Check(j.Source != "PostingAgent" || channels.Count > 0, "channels", "Pick at least one place to post this requirement")
            .ThrowIfInvalid();

        var title = j.Title!.Trim();
        var client = j.Client!.Trim();
        if (!force)
        {
            var by = await db.Requirements.Where(r => r.Status == "Open" && r.Title == title && r.EndClient == client)
                .Select(r => r.PostedBy.FullName).FirstOrDefaultAsync();
            if (by != null)
                throw AppException.Conflict("DUPLICATE_REQUIREMENT", "Possible duplicate requirement", $"\"{title}\" for {client} is already open (posted by {by}). Post anyway?");
        }

        var req = new Requirement
        {
            Title = title, EndClient = client, Vendor = string.IsNullOrWhiteSpace(j.Vendor) ? "Direct client" : j.Vendor.Trim(),
            Tier = string.IsNullOrEmpty(j.Tier) ? "Direct" : j.Tier, Location = string.IsNullOrWhiteSpace(j.Location) ? "Remote" : j.Location.Trim(),
            WorkMode = j.Mode!, ContractType = j.Type!, BillRate = j.Rate, Skills = skills,
            AcceptedVisas = j.Visas is { Count: > 0 } v ? v : ["Any"], Description = j.Description,
            Source = j.Source is "PostingAgent" or "SourcingAgent" ? j.Source : "Manual", PostedByUserId = me.RequiredId,
        };
        db.Requirements.Add(req);
        await db.SaveChangesAsync();
        if (channels.Count > 0)
        {
            db.JobPostings.AddRange(channels.Select(ch => new JobPosting { RequirementId = req.RequirementId, Channel = ch }));
            await db.SaveChangesAsync();
        }
        await audit.LogAsync("REQ_CREATE", $"Posted \"{title}\" for {client}{(channels.Count > 0 ? $"; queued for {channels.Count} channels" : "")}", "Requirement", req.RequirementId);
        return await data.RequirementAsync(req.RequirementId);
    }

    /// <summary>Opens or closes a requirement. Closing one with active submissions returns 409 unless <paramref name="force"/> is true.</summary>
    [HttpPatch("{id:int}/status"), RequirePerm("jobs.close")]
    public async Task<RequirementDto> SetStatus(int id, StatusInput input, [FromQuery] bool force = false)
    {
        if (input.Status is not ("Open" or "Closed")) throw AppException.Bad("VALIDATION", "Invalid status", "Status must be Open or Closed.");
        var r = await data.RequirementAsync(id);
        if (input.Status == "Closed" && r.ActiveSubmissions > 0 && !force)
            throw AppException.Conflict("HAS_ACTIVE_SUBMISSIONS", "Requirement has active submissions",
                $"\"{r.Title}\" has {r.ActiveSubmissions} active submission(s). Closing hides it from the job board and matching. Close anyway?");
        await db.Requirements.Where(x => x.RequirementId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, input.Status));
        await audit.LogAsync("REQ_STATUS", $"{(input.Status == "Open" ? "Reopened" : "Closed")} \"{r.Title}\"", "Requirement", id);
        return await data.RequirementAsync(id);
    }

    /// <summary>Saves the requirement to your list, or removes it if already saved.</summary>
    [HttpPost("{id:int}/save"), RequirePerm("board.view")]
    public async Task<object> ToggleSave(int id)
    {
        await data.RequirementAsync(id);
        var meId = me.RequiredId;
        var existing = await db.SavedRequirements.FirstOrDefaultAsync(s => s.UserId == meId && s.RequirementId == id);
        if (existing != null) db.SavedRequirements.Remove(existing);
        else db.SavedRequirements.Add(new SavedRequirement { UserId = meId, RequirementId = id });
        await db.SaveChangesAsync();
        return new { saved = existing == null };
    }
}
