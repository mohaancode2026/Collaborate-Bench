using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>
/// Jobs from an outside job-search API, scored against the bench. Applying a candidate turns the job into a normal
/// requirement (source "External") and creates a submission, so it shows up in the pipeline, reports and onboarding.
/// </summary>
[ApiController, Route("api/external-jobs")]
public sealed class ExternalJobsController(AppDbContext db, DataService data, JobFeedService feed, SubmissionService submissions,
    ICurrentUser me, AuditService audit) : ControllerBase
{
    /// <summary>Whether job search is configured, today's API usage against the daily limit, and how many jobs are saved.</summary>
    [HttpGet("status"), RequirePerm("board.view")]
    public async Task<JobFeedStatus> Status()
    {
        var o = feed.Options;
        var last = await db.ExternalJobSearches.Where(s => s.Succeeded && !s.CacheHit).MaxAsync(s => (DateTime?)s.CreatedUtc);
        return new JobFeedStatus(o.Configured, o.Provider, await feed.CallsTodayAsync(), o.DailyCallLimit, o.AutoRefreshHours, last,
            await db.ExternalJobs.CountAsync(j => !j.IsHidden));
    }

    /// <summary>Saved jobs (all recent ones, or one search's results), each with the best consultants in the user's view.</summary>
    [HttpGet, RequirePerm("board.view")]
    public async Task<List<ExternalJobDto>> List([FromQuery] int? searchId, [FromQuery] string? q, [FromQuery] int? consultantId,
        [FromQuery] bool remoteOnly = false, [FromQuery] bool linkedIn = false, [FromQuery] int? minScore = null, [FromQuery] bool hidden = false, [FromQuery] string? scope = null)
    {
        var jobs = db.ExternalJobs.AsNoTracking().Where(j => j.IsHidden == hidden);
        if (searchId is int sid)
        {
            var ids = (await db.ExternalJobSearches.AsNoTracking().FirstOrDefaultAsync(s => s.SearchId == sid) ?? throw AppException.NotFound("Search")).JobIds;
            jobs = jobs.Where(j => ids.Contains(j.ExternalJobId));
        }
        else
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, feed.Options.KeepDays));
            jobs = jobs.Where(j => j.LastSeenUtc >= cutoff);
        }
        if (remoteOnly) jobs = jobs.Where(j => j.IsRemote);
        IEnumerable<ExternalJob> list = await jobs.OrderByDescending(j => j.LastSeenUtc).Take(300).ToListAsync();
        if (linkedIn) list = list.Where(JobText.OnLinkedIn);
        if (!string.IsNullOrWhiteSpace(q))
            list = list.Where(j => $"{j.Title} {j.Company} {j.Location} {j.Publisher} {string.Join(' ', j.Skills)}".Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
        var rows = await ScoreAsync(list.ToList(), consultantId, scope, 3);
        return minScore is > 0 ? rows.Where(r => r.TopScore >= minScore).ToList() : rows;
    }

    /// <summary>Calls the provider (or reuses a recent identical search). With a consultant, searches for their title and location.</summary>
    [HttpPost("search"), RequirePerm("board.view"), EnableRateLimiting("ai")]
    public async Task<ExternalSearchResult> Search(ExternalSearchInput input, CancellationToken ct)
    {
        var skills = (input.Skills ?? []).Select(s => s.Trim()).Where(s => s.Length is > 0 and <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        var query = string.Join(" ", new[] { input.Query?.Trim() ?? "" }.Concat(skills).Where(s => s.Length > 0));
        var location = input.Location?.Trim();
        var remote = input.RemoteOnly;
        ConsultantDto? c = null;
        if (input.ConsultantId is int cid)
        {
            c = await data.ConsultantAsync(cid, ct);   // must be in the user's view
            if (query.Length == 0) query = JobText.QueryFor(c.Title, c.Skills, includeTitle: input.IncludeTitle);
            else if (input.IncludeTitle && !string.IsNullOrWhiteSpace(c.Title)) query = $"{c.Title.Trim()} {query}";
            if (string.IsNullOrWhiteSpace(location) && !remote)
            {
                if (string.IsNullOrWhiteSpace(c.Location) || c.Location.Contains("remote", StringComparison.OrdinalIgnoreCase)) remote = true;
                else location = c.Location;
            }
        }
        new Validator()
            .Check(query.Length >= 2, "query", "Enter a job title or skill, or pick a consultant")
            .Check(query.Length <= 200, "query", "Keep the search under 200 characters")
            .Check((location?.Length ?? 0) <= 100, "location", "Keep the location under 100 characters")
            .ThrowIfInvalid();

        var (search, added) = await feed.SearchAsync(query, location, remote, JobFeedService.NormalizeDate(input.DatePosted), input.ContractOnly,
            me.RequiredId, false, ct);
        if (!search.CacheHit)
            await audit.LogAsync("JOBFEED_SEARCH", $"Searched {feed.Options.Provider} for \"{query}\"{(location != null ? $" in {location}" : "")}{(remote ? " (remote)" : "")}: {search.ResultCount} jobs, {added} new");

        var ids = search.JobIds;
        var jobs = ids.Count == 0 ? [] : await db.ExternalJobs.AsNoTracking().Where(j => ids.Contains(j.ExternalJobId) && !j.IsHidden).ToListAsync(ct);
        return new ExternalSearchResult(search.SearchId, query, location, search.CacheHit, search.ResultCount, added, await ScoreAsync(jobs, c?.Id, null, 3));
    }

    /// <summary>Every consultant in the user's view scored against one job (for the apply dialog).</summary>
    [HttpGet("{id:int}/matches"), RequirePerm("match.view")]
    public async Task<List<MatchResult>> Matches(int id, [FromQuery] string? scope)
    {
        var job = await db.ExternalJobs.AsNoTracking().FirstOrDefaultAsync(j => j.ExternalJobId == id) ?? throw AppException.NotFound("Job");
        return (await ScoreAsync([job], null, scope, int.MaxValue))[0].Matches;
    }

    /// <summary>Turn the job into an open requirement without submitting anyone yet.</summary>
    [HttpPost("{id:int}/import"), RequirePerm("jobs.create")]
    public async Task<object> Import(int id, ExternalImportInput input)
    {
        var job = await db.ExternalJobs.FirstOrDefaultAsync(j => j.ExternalJobId == id) ?? throw AppException.NotFound("Job");
        if (await ExistingRequirementAsync(job) is int existing)
            return new { requirement = await data.RequirementAsync(existing), linkedExisting = true };
        var v = new Validator();
        CheckImport(v, input.BillRate, input.Type, input.Tier, input.Skills);
        v.ThrowIfInvalid();
        var (reqId, linked) = await CreateRequirementAsync(job, input.BillRate, input.Type, input.Vendor, input.Tier, input.Skills, input.Visas);
        return new { requirement = await data.RequirementAsync(reqId), linkedExisting = linked };
    }

    /// <summary>
    /// Apply a candidate: imports the job (if needed) and submits the consultant with RTR, through the same rules as any submission.
    /// The recruiter still completes the application on the employer's / job board's page (applyUrl).
    /// </summary>
    [HttpPost("{id:int}/apply"), RequirePerm("sub.create")]
    public async Task<object> Apply(int id, ExternalApplyInput input)
    {
        var job = await db.ExternalJobs.FirstOrDefaultAsync(j => j.ExternalJobId == id) ?? throw AppException.NotFound("Job");
        var c = await data.ConsultantAsync(input.ConsultantId);
        SubmissionService.EnsureAvailable(c);

        var existing = await ExistingRequirementAsync(job);
        if (existing is null) me.Require("jobs.create");   // applying here also posts the job as a requirement
        var billRate = existing is int rid ? (await data.RequirementAsync(rid)).Rate : input.BillRate;

        // Check everything first so a mistake never leaves a half-done import behind.
        var v = SubmissionService.Validate(new Validator(), input.RtrSigned, input.SubmitRate, input.PayRate, billRate, input.Notes);
        if (existing is null) CheckImport(v, input.BillRate, input.Type, input.Tier, input.Skills);
        v.ThrowIfInvalid();

        var (reqId, linked) = existing is int e ? (e, true)
            : await CreateRequirementAsync(job, input.BillRate, input.Type, input.Vendor, input.Tier, input.Skills, input.Visas);
        var (submission, warnings) = await submissions.CreateAsync(
            new SubmissionInput(c.Id, reqId, input.SubmitRate, input.PayRate, input.RtrSigned, input.Notes),
            $" from an external job ({job.Publisher ?? feed.Options.Provider})");
        return new { submission, warnings, applyUrl = job.ApplyUrl, publisher = job.Publisher, requirementId = reqId, linkedExisting = linked };
    }

    /// <summary>Hides the job from results (or shows it again). Shared for the whole company.</summary>
    [HttpPost("{id:int}/hide"), RequirePerm("jobs.create")]
    public async Task<object> ToggleHidden(int id)
    {
        var job = await db.ExternalJobs.FirstOrDefaultAsync(j => j.ExternalJobId == id) ?? throw AppException.NotFound("Job");
        job.IsHidden = !job.IsHidden;
        await db.SaveChangesAsync();
        await audit.LogAsync(job.IsHidden ? "JOBFEED_HIDE" : "JOBFEED_UNHIDE", $"{(job.IsHidden ? "Hid" : "Restored")} external job \"{job.Title}\" ({job.Company})");
        return new { hidden = job.IsHidden };
    }

    // ---------- helpers ----------

    private static void CheckImport(Validator v, decimal billRate, string? type, string? tier, List<string>? skills) => v
        .Check(billRate is >= 10 and <= 400, "billRate", "Enter the bill rate for this job ($10–$400/hr). Most job boards don't show it; ask the vendor or use your estimate.")
        .Check(type != null && Domain.ContractTypes.Contains(type), "type", "Choose C2C, W2 or 1099")
        .Check(string.IsNullOrEmpty(tier) || Domain.Tiers.Contains(tier), "tier", "Choose a valid vendor tier")
        .Check(CleanSkills(skills).Count > 0, "skills", "Add at least one required skill");

    private static List<string> CleanSkills(List<string>? skills) =>
        (skills ?? []).Select(s => s.Trim()).Where(s => s.Length is > 0 and <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();

    /// <summary>The requirement this job already became, if it still exists.</summary>
    private async Task<int?> ExistingRequirementAsync(ExternalJob job) =>
        job.RequirementId is int rid && await db.Requirements.AnyAsync(r => r.RequirementId == rid) ? rid : null;

    private async Task<(int RequirementId, bool Linked)> CreateRequirementAsync(ExternalJob job, decimal billRate, string? type, string? vendor,
        string? tier, List<string>? skills, List<string>? visas)
    {
        // Same job already posted by hand (same title and client, still open): link to it instead of making a duplicate.
        var dup = await db.Requirements.Where(r => r.Status == "Open" && r.Title == job.Title && r.EndClient == job.Company)
            .Select(r => (int?)r.RequirementId).FirstOrDefaultAsync();
        var linked = dup is not null;
        if (dup is int d) job.RequirementId = d;
        else
        {
            var cleanVisas = (visas ?? []).Where(x => x == "Any" || Domain.Visas.Contains(x)).Distinct().ToList();
            var req = new Requirement
            {
                Title = job.Title, EndClient = job.Company,
                Vendor = string.IsNullOrWhiteSpace(vendor) ? "Direct client" : vendor.Trim(),
                Tier = string.IsNullOrEmpty(tier) ? "Direct" : tier,
                Location = string.IsNullOrWhiteSpace(job.Location) ? "Remote" : job.Location,
                WorkMode = JobText.Mode(job), ContractType = type!, BillRate = billRate, Skills = CleanSkills(skills),
                AcceptedVisas = cleanVisas.Count > 0 ? cleanVisas : ["Any"],
                Description = $"{job.Description}\n\n---\nImported from {job.Publisher ?? "a job board"} via {job.Provider}.\nApply: {job.ApplyUrl}",
                Source = "External", PostedByUserId = me.RequiredId,
            };
            db.Requirements.Add(req);
            await db.SaveChangesAsync();
            job.RequirementId = req.RequirementId;
        }
        await db.SaveChangesAsync();
        await audit.LogAsync("REQ_IMPORT", linked
            ? $"Linked external job \"{job.Title}\" ({job.Company}) to the existing requirement"
            : $"Imported \"{job.Title}\" for {job.Company} from {job.Publisher ?? job.Provider} as a requirement", "Requirement", job.RequirementId);
        return (job.RequirementId!.Value, linked);
    }

    /// <summary>Scores jobs against Bench/Marketing consultants in the user's view (or one consultant), best first.</summary>
    private async Task<List<ExternalJobDto>> ScoreAsync(List<ExternalJob> jobs, int? consultantId, string? scope, int take)
    {
        var consultants = consultantId is int cid
            ? [await data.ConsultantAsync(cid)]
            : (await data.ConsultantsAsync(scope)).Where(c => c.Status is "Bench" or "Marketing").ToList();
        var reqIds = jobs.Where(j => j.RequirementId != null).Select(j => j.RequirementId!.Value).ToHashSet();
        var stages = reqIds.Count == 0 ? new Dictionary<(int, int), string>()
            : (await data.SubmissionsAsync(null)).Where(s => reqIds.Contains(s.RequirementId))
                .GroupBy(s => (s.ConsultantId, s.RequirementId)).ToDictionary(g => g.Key, g => g.First().Stage);

        return jobs.Select(j =>
        {
            var r = JobText.AsRequirement(j);
            var matches = consultants
                .Select(c => Matching.Score(c, r, j.RequirementId is int rid ? stages.GetValueOrDefault((c.Id, rid)) : null))
                .OrderByDescending(m => m.Score).ToList();
            return new ExternalJobDto
            {
                Id = j.ExternalJobId, Provider = j.Provider, Publisher = j.Publisher, Title = j.Title, Company = j.Company, Location = j.Location,
                Remote = j.IsRemote, Mode = r.Mode, EmploymentType = j.EmploymentType, SuggestedType = r.Type, MinRate = j.MinHourly, MaxRate = j.MaxHourly,
                Skills = j.Skills, Visas = j.Visas, Description = j.Description, ApplyUrl = j.ApplyUrl, PostedUtc = j.PostedUtc,
                ApplyOptions = j.ApplyOptions.Count > 0 ? j.ApplyOptions : [new(j.Publisher ?? "Job board", j.ApplyUrl, false)],
                OnLinkedIn = JobText.OnLinkedIn(j), ContactEmails = JobText.ContactEmails(j.Description),
                DaysOld = j.PostedUtc is DateTime p ? Math.Max(0, (int)(DateTime.UtcNow - p).TotalDays) : null,
                FetchedUtc = j.FetchedUtc, RequirementId = j.RequirementId, Hidden = j.IsHidden,
                TopScore = matches.FirstOrDefault()?.Score ?? 0, Matches = matches.Take(take).ToList(),
            };
        })
        .OrderByDescending(d => d.TopScore).ThenByDescending(d => d.PostedUtc).ToList();
    }
}
