using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Services;

/// <summary>
/// Shared, permission-aware reads (EF Core). The global query filters in AppDbContext already limit consultants and
/// submissions to the user's maximum scope; the optional "scope" (mine/team/all) narrows it further for the UI.
/// </summary>
public sealed class DataService(AppDbContext db, ICurrentUser me)
{
    private static readonly string[] Closed = ["Billing", "Withdrawn"];

    // ---------- scope helpers ----------
    /// <param name="leads">null: everyone; false: leave out sourced leads (bench views); true: only leads.</param>
    private IQueryable<Consultant> ConsultantScope(string? scope, bool? leads = false)
    {
        var q = db.Consultants.AsNoTracking().Where(c => c.Status != "Inactive");
        if (leads == true) q = q.Where(c => c.Status == "Lead");
        else if (leads == false) q = q.Where(c => c.Status != "Lead");
        var meId = me.RequiredId;
        return (scope ?? "").ToLowerInvariant() switch
        {
            "mine" => q.Where(c => c.OwnerUserId == meId),
            "team" when me.DataScope != "OWN" => q.Where(c => db.UserHierarchy.Any(h => h.AncestorUserId == meId && h.DescendantUserId == c.OwnerUserId)),
            _ => q,
        };
    }

    private IQueryable<Submission> SubmissionScope(string? scope)
    {
        var q = db.Submissions.AsNoTracking().Where(s => s.Stage != "Withdrawn");
        var meId = me.RequiredId;
        return (scope ?? "").ToLowerInvariant() switch
        {
            "mine" => q.Where(s => s.OwnerUserId == meId),
            "team" when me.DataScope != "OWN" => q.Where(s => db.UserHierarchy.Any(h => h.AncestorUserId == meId && h.DescendantUserId == s.OwnerUserId)),
            _ => q,
        };
    }

    /// <summary>Users whose activity this user may see (sec tables have no RLS, so filter here).</summary>
    public IQueryable<User> VisibleUsers()
    {
        var meId = me.RequiredId;
        var q = db.Users.AsNoTracking();
        return me.DataScope switch
        {
            "ALL" => q,
            "TEAM" => q.Where(u => db.UserHierarchy.Any(h => h.AncestorUserId == meId && h.DescendantUserId == u.UserId)),
            _ => q.Where(u => u.UserId == meId),
        };
    }

    /// <summary>Active users in the chosen dashboard scope ("team" = me and everyone below me), for per-recruiter stats.</summary>
    public async Task<List<(int Id, string Name, string Role, string? Team)>> ScopeUsersAsync(string? scope, CancellationToken ct = default)
    {
        var meId = me.RequiredId;
        var q = VisibleUsers().Where(u => u.IsActive);
        if ((scope ?? "").Equals("team", StringComparison.OrdinalIgnoreCase))
            q = q.Where(u => db.UserHierarchy.Any(h => h.AncestorUserId == meId && h.DescendantUserId == u.UserId));
        var rows = await q.Select(u => new { u.UserId, u.FullName, Role = u.Role.Name, Team = u.Team != null ? u.Team.Name : null }).ToListAsync(ct);
        return rows.Select(u => (u.UserId, u.FullName, u.Role, u.Team)).ToList();
    }

    /// <summary>Owners must be inside the user's data scope (replaces the RLS block predicate, which SQL Server 2012 lacks).</summary>
    public async Task EnsureCanAssignOwnerAsync(int ownerUserId, CancellationToken ct = default)
    {
        if (!await VisibleUsers().AnyAsync(u => u.UserId == ownerUserId && u.IsActive, ct))
            throw AppException.Forbidden("That change would move the record outside your team, which your role isn't allowed to do.");
    }

    private static int Days(DateTime utc) => Math.Max(0, (int)(DateTime.UtcNow - utc).TotalDays);

    // ---------- consultants ----------
    private IQueryable<ConsultantDto> ProjectConsultants(IQueryable<Consultant> q) => q
        .OrderByDescending(c => c.CreatedUtc)
        .Select(c => new ConsultantDto
        {
            Id = c.ConsultantId, Name = c.FullName, Title = c.Title ?? "", Email = c.Email ?? "", Phone = c.Phone ?? "",
            Visa = c.VisaStatus ?? "", Location = c.Location ?? "", Relocate = c.OpenToRelocate, Rate = c.PayRate,
            Exp = c.ExperienceYears ?? 0, Status = c.Status, Skills = c.Skills, Summary = c.HotlistSummary ?? "",
            OwnerId = c.OwnerUserId, OwnerName = c.Owner.FullName, OwnerTeam = c.Owner.Team != null ? c.Owner.Team.Name : null,
            OwnerRole = c.Owner.Role.Name, Documents = db.ConsultantDocuments.Count(d => d.ConsultantId == c.ConsultantId),
            CreatedUtc = c.CreatedUtc,
            ActiveSubmissions = db.Submissions.Count(s => s.ConsultantId == c.ConsultantId && !Closed.Contains(s.Stage)),
            LinkedInUrl = c.LinkedInUrl, Source = c.Source,
            PassportOnFile = c.PassportLast4 != null ? "••••" + c.PassportLast4 : null,
            VisaValidUntil = c.VisaValidUntil, Availability = c.Availability, PreferredLocations = c.PreferredLocations,
            Education = c.Education, Certifications = c.Certifications, UsExp = c.UsExperienceYears,
            RowVersion = Convert.ToBase64String(c.RowVer),
        });

    /// <summary>Consultants in the user's view. Sourced leads are left out unless <paramref name="leadsOnly"/> is set.</summary>
    public async Task<List<ConsultantDto>> ConsultantsAsync(string? scope, CancellationToken ct = default, bool leadsOnly = false) =>
        (await ProjectConsultants(ConsultantScope(scope, leadsOnly ? true : false)).ToListAsync(ct)).Select(Finish).ToList();

    /// <summary>Every available consultant (Bench or Marketing) across the company, for the hotlist. Callers must hold
    /// hotlist.send: this deliberately looks past the caller's team filter because the hotlist goes out for the whole bench.</summary>
    public async Task<List<ConsultantDto>> HotlistConsultantsAsync(CancellationToken ct = default)
    {
        me.Require("hotlist.send");
        var q = db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(c => c.Status == "Bench" || c.Status == "Marketing");
        return (await ProjectConsultants(q).ToListAsync(ct)).Select(Finish).ToList();
    }

    public async Task<ConsultantDto> ConsultantAsync(int id, CancellationToken ct = default) =>
        Finish(await ProjectConsultants(ConsultantScope(null, leads: null).Where(c => c.ConsultantId == id)).FirstOrDefaultAsync(ct)
               ?? throw AppException.NotFound("Consultant"));

    private static ConsultantDto Finish(ConsultantDto c) { c.DaysOnBench = Days(c.CreatedUtc); return c; }

    // ---------- requirements ----------
    private IQueryable<RequirementDto> ProjectRequirements(IQueryable<Requirement> q)
    {
        var meId = me.RequiredId;
        return q.AsNoTracking().OrderByDescending(r => r.PostedUtc).Select(r => new RequirementDto
        {
            Id = r.RequirementId, Title = r.Title, Client = r.EndClient, Vendor = r.Vendor, Tier = r.Tier, Location = r.Location,
            Mode = r.WorkMode, Type = r.ContractType, Rate = r.BillRate, Skills = r.Skills, Visas = r.AcceptedVisas,
            Description = r.Description, Source = r.Source, Status = r.Status, PostedById = r.PostedByUserId,
            PostedByName = r.PostedBy.FullName, PostedUtc = r.PostedUtc,
            Saved = db.SavedRequirements.Any(s => s.RequirementId == r.RequirementId && s.UserId == meId),
            ActiveSubmissions = db.Submissions.Count(s => s.RequirementId == r.RequirementId && !Closed.Contains(s.Stage)),
        });
    }

    private static RequirementDto Finish(RequirementDto r) { r.DaysOld = Math.Max(0, (int)(DateTime.UtcNow.Date - r.PostedUtc.Date).TotalDays); return r; }

    public async Task<List<RequirementDto>> RequirementsAsync(bool openOnly, CancellationToken ct = default) =>
        (await ProjectRequirements(openOnly ? db.Requirements.Where(r => r.Status == "Open") : db.Requirements).ToListAsync(ct)).Select(Finish).ToList();

    public async Task<RequirementDto> RequirementAsync(int id, CancellationToken ct = default) =>
        Finish(await ProjectRequirements(db.Requirements.Where(r => r.RequirementId == id)).FirstOrDefaultAsync(ct)
               ?? throw AppException.NotFound("Requirement"));

    // ---------- submissions ----------
    private IQueryable<SubmissionDto> ProjectSubmissions(IQueryable<Submission> q) => Project(q.OrderByDescending(s => s.StageChangedUtc));

    private static readonly string[] StageOrder = ["Submitted", "Vendor Screen", "Client Interview", "Offer", "MSA / PO", "Billing"];

    /// <summary>
    /// One page of submissions for the grid, filtered, searched and sorted in the database, plus what the grid shows around it:
    /// counts per stage (for the tabs), how many are stuck 3+ days, and the executives in the list (for the filter).
    /// </summary>
    public async Task<SubmissionPage> SubmissionPageAsync(SubmissionPageQuery p, CancellationToken ct = default)
    {
        var q = SubmissionScope(p.Scope);
        if (!string.IsNullOrWhiteSpace(p.Q))
        {
            var t = p.Q.Trim();
            q = q.Where(s => s.Consultant.FullName.Contains(t) || (s.Consultant.Title ?? "").Contains(t) || s.Requirement.Title.Contains(t)
                             || s.Requirement.EndClient.Contains(t) || s.Requirement.Vendor.Contains(t) || s.Owner.FullName.Contains(t)
                             || s.Requirement.ContractType.Contains(t) || (s.Notes ?? "").Contains(t));
        }
        // The executive filter's own list ignores the executive filter, so you can switch between people.
        var owners = await q.GroupBy(s => new { s.OwnerUserId, s.Owner.FullName })
            .Select(g => new OwnerCount(g.Key.OwnerUserId, g.Key.FullName, g.Count())).ToListAsync(ct);
        if (p.Owner is int oid) q = q.Where(s => s.OwnerUserId == oid);

        var stageCounts = (await q.GroupBy(s => s.Stage).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        var staleBefore = DateTime.UtcNow.AddDays(-3);
        if (!string.IsNullOrEmpty(p.Stage)) q = q.Where(s => s.Stage == p.Stage);
        var staleCount = await q.CountAsync(s => s.StageChangedUtc <= staleBefore && s.Stage != "Billing", ct);
        if (p.StaleOnly) q = q.Where(s => s.StageChangedUtc <= staleBefore && s.Stage != "Billing");
        var total = await q.CountAsync(ct);

        var asc = p.Dir != "desc";
        IOrderedQueryable<Submission> sorted = (p.Sort ?? "stage") switch
        {
            "consultant" => asc ? q.OrderBy(s => s.Consultant.FullName) : q.OrderByDescending(s => s.Consultant.FullName),
            "requirement" => asc ? q.OrderBy(s => s.Requirement.Title).ThenBy(s => s.Requirement.EndClient) : q.OrderByDescending(s => s.Requirement.Title).ThenByDescending(s => s.Requirement.EndClient),
            "rate" => asc ? q.OrderBy(s => s.SubmitRate) : q.OrderByDescending(s => s.SubmitRate),
            "margin" => asc ? q.OrderBy(s => s.SubmitRate - s.PayRate) : q.OrderByDescending(s => s.SubmitRate - s.PayRate),
            "match" => asc ? q.OrderBy(s => s.MatchScore ?? -1) : q.OrderByDescending(s => s.MatchScore ?? -1),
            "days" => asc ? q.OrderByDescending(s => s.StageChangedUtc) : q.OrderBy(s => s.StageChangedUtc),   // fewer days in stage = changed later
            "owner" => asc ? q.OrderBy(s => s.Owner.FullName) : q.OrderByDescending(s => s.Owner.FullName),
            "created" => asc ? q.OrderBy(s => s.CreatedUtc) : q.OrderByDescending(s => s.CreatedUtc),
            // Pipeline order (Submitted → Billing), longest waiting first within a stage.
            _ => (asc ? q.OrderBy(s => s.Stage == StageOrder[0] ? 0 : s.Stage == StageOrder[1] ? 1 : s.Stage == StageOrder[2] ? 2 : s.Stage == StageOrder[3] ? 3 : s.Stage == StageOrder[4] ? 4 : 5)
                      : q.OrderByDescending(s => s.Stage == StageOrder[0] ? 0 : s.Stage == StageOrder[1] ? 1 : s.Stage == StageOrder[2] ? 2 : s.Stage == StageOrder[3] ? 3 : s.Stage == StageOrder[4] ? 4 : 5))
                 .ThenBy(s => s.StageChangedUtc),
        };
        var size = Math.Clamp(p.PageSize, 10, 100);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        var page = Math.Clamp(p.Page, 1, pages);
        var items = (await Project(sorted.ThenByDescending(s => s.SubmissionId).Skip((page - 1) * size).Take(size)).ToListAsync(ct)).Select(Finish).ToList();
        return new SubmissionPage(items, total, page, size, stageCounts, staleCount, owners.OrderBy(o => o.Name).ToList());
    }

    private static IQueryable<SubmissionDto> Project(IQueryable<Submission> q) => q
        .Select(s => new SubmissionDto
        {
            Id = s.SubmissionId, ConsultantId = s.ConsultantId, ConsultantName = s.Consultant.FullName, ConsultantTitle = s.Consultant.Title ?? "",
            RequirementId = s.RequirementId, Title = s.Requirement.Title, Client = s.Requirement.EndClient, Vendor = s.Requirement.Vendor,
            Type = s.Requirement.ContractType, BillRate = s.Requirement.BillRate, Stage = s.Stage, SubmitRate = s.SubmitRate, PayRate = s.PayRate,
            MatchScore = s.MatchScore, Rtr = s.RtrSigned, Notes = s.Notes, OwnerId = s.OwnerUserId, OwnerName = s.Owner.FullName,
            StageChangedUtc = s.StageChangedUtc, CreatedUtc = s.CreatedUtc, RowVersion = Convert.ToBase64String(s.RowVer),
        });

    private SubmissionDto Finish(SubmissionDto s)
    {
        s.DaysInStage = Days(s.StageChangedUtc);
        s.MarginPerHour = me.Has("margin.view") ? s.SubmitRate - s.PayRate : null;
        return s;
    }

    public async Task<List<SubmissionDto>> SubmissionsAsync(string? scope, CancellationToken ct = default) =>
        (await ProjectSubmissions(SubmissionScope(scope)).ToListAsync(ct)).Select(Finish).ToList();

    public async Task<SubmissionDto> SubmissionAsync(int id, CancellationToken ct = default) =>
        Finish(await ProjectSubmissions(SubmissionScope(null).Where(s => s.SubmissionId == id)).FirstOrDefaultAsync(ct)
               ?? throw AppException.NotFound("Submission"));

    // ---------- activity / audit ----------
    private static readonly string[] AuthActions = ["LOGIN_OK", "LOGIN_FAIL", "LOGOUT"];

    /// <summary>One page of the audit log (people in your view), newest first, with the total for the pager.
    /// <paramref name="q"/> searches the user, action and detail.</summary>
    public async Task<PagedResult<ActivityDto>> AuditPageAsync(string? outcome, string? q, int page, int pageSize, CancellationToken ct = default)
    {
        var visible = VisibleUsers().Select(u => u.UserId);
        var rows = db.AuditLogs.AsNoTracking().Where(a => a.UserId != null && visible.Contains(a.UserId.Value));
        if (outcome != null) rows = rows.Where(a => a.Outcome == outcome);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            rows = rows.Where(a => a.Action.Contains(t) || (a.Detail ?? "").Contains(t) || (a.User != null && a.User.FullName.Contains(t)));
        }
        var total = await rows.CountAsync(ct);
        var size = Math.Clamp(pageSize, 10, 200);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        var p = Math.Clamp(page, 1, pages);
        var items = await rows.OrderByDescending(a => a.AuditId).Skip((p - 1) * size).Take(size)
            .Select(a => new ActivityDto(a.AuditId, a.User != null ? a.User.FullName : "Unknown", a.User != null ? a.User.Role.Name : "",
                                         a.Action, a.Detail ?? "", a.Outcome, a.CreatedUtc))
            .ToListAsync(ct);
        return new PagedResult<ActivityDto>(items, total, p, size);
    }

    public Task<List<ActivityDto>> ActivityAsync(int take, string? outcome, bool includeAuth, CancellationToken ct = default)
    {
        var visible = VisibleUsers().Select(u => u.UserId);
        var q = db.AuditLogs.AsNoTracking().Where(a => a.UserId != null && visible.Contains(a.UserId.Value));
        if (outcome != null) q = q.Where(a => a.Outcome == outcome);
        if (!includeAuth) q = q.Where(a => !AuthActions.Contains(a.Action));
        return q.OrderByDescending(a => a.AuditId).Take(Math.Clamp(take, 1, 500))
            .Select(a => new ActivityDto(a.AuditId, a.User != null ? a.User.FullName : "Unknown", a.User != null ? a.User.Role.Name : "",
                                         a.Action, a.Detail ?? "", a.Outcome, a.CreatedUtc))
            .ToListAsync(ct);
    }
}
