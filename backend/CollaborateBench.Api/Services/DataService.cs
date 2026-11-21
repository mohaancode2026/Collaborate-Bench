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
    private IQueryable<Consultant> ConsultantScope(string? scope)
    {
        var q = db.Consultants.AsNoTracking().Where(c => c.Status != "Inactive");
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
            CreatedUtc = c.CreatedUtc,
            ActiveSubmissions = db.Submissions.Count(s => s.ConsultantId == c.ConsultantId && !Closed.Contains(s.Stage)),
            RowVersion = Convert.ToBase64String(c.RowVer),
        });

    public async Task<List<ConsultantDto>> ConsultantsAsync(string? scope, CancellationToken ct = default) =>
        (await ProjectConsultants(ConsultantScope(scope)).ToListAsync(ct)).Select(Finish).ToList();

    public async Task<ConsultantDto> ConsultantAsync(int id, CancellationToken ct = default) =>
        Finish(await ProjectConsultants(ConsultantScope(null).Where(c => c.ConsultantId == id)).FirstOrDefaultAsync(ct)
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
    private IQueryable<SubmissionDto> ProjectSubmissions(IQueryable<Submission> q) => q
        .OrderByDescending(s => s.StageChangedUtc)
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
