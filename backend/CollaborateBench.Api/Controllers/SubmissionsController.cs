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

/// <summary>Submissions (consultant → requirement) through the pipeline stages, plus the onboarding checklist.</summary>
[ApiController, Route("api/submissions")]
public sealed class SubmissionsController(AppDbContext db, DataService data, ICurrentUser me, AuditService audit, SubmissionService submissions) : ControllerBase
{
    /// <summary>Submissions in your view (withdrawn ones excluded).</summary>
    [HttpGet, RequirePerm("pipeline.view")]
    public Task<List<SubmissionDto>> List([FromQuery] string? scope) => data.SubmissionsAsync(scope);

    /// <summary>One page of the submissions grid: filtered, searched and sorted in the database (see <see cref="SubmissionPageQuery"/>).</summary>
    [HttpGet("page"), RequirePerm("pipeline.view")]
    public Task<SubmissionPage> Page([FromQuery] string? scope, [FromQuery] string? stage, [FromQuery] string? q, [FromQuery] int? owner,
        [FromQuery] bool stale = false, [FromQuery] string? sort = null, [FromQuery] string? dir = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default) =>
        data.SubmissionPageAsync(new SubmissionPageQuery(scope, stage, q, owner, stale, sort, dir, page, pageSize), ct);

    /// <summary>Submits a consultant with RTR. Rules live in SubmissionService; returns the submission plus non-blocking warnings.</summary>
    [HttpPost, RequirePerm("sub.create")]
    public async Task<object> Create(SubmissionInput input)
    {
        var (submission, warnings) = await submissions.CreateAsync(input);
        return new { submission, warnings };
    }

    /// <summary>Stage rules (permissions, onboarding gate, concurrency, history, audit) live in bench.usp_MoveSubmissionStage.</summary>
    [HttpPost("{id:int}/stage"), RequirePerm("sub.move")]
    public async Task<SubmissionDto> Move(int id, StageInput input)
    {
        if (string.IsNullOrEmpty(input.ToStage) || !Domain.Stages.Contains(input.ToStage))
            throw AppException.Bad("INVALID_STAGE", "Invalid stage", $"Stage must be one of: {string.Join(", ", Domain.Stages)}.");
        byte[]? ver = null;
        if (!string.IsNullOrEmpty(input.RowVersion))
        {
            try { ver = Convert.FromBase64String(input.RowVersion); }
            catch (FormatException) { throw AppException.Bad("VALIDATION", "Invalid version", "Reload the page and try again."); }
        }
        await db.Database.ExecuteSqlRawAsync(
            "EXEC bench.usp_MoveSubmissionStage @SubmissionId, @ToStage, @ExpectedRowVer",
            new SqlParameter("@SubmissionId", SqlDbType.Int) { Value = id },
            new SqlParameter("@ToStage", SqlDbType.VarChar, 20) { Value = input.ToStage },
            new SqlParameter("@ExpectedRowVer", SqlDbType.VarBinary, 8) { Value = (object?)ver ?? DBNull.Value });
        return await data.SubmissionAsync(id);
    }

    /// <summary>Withdraws a submission. Billing placements can't be withdrawn (end the assignment instead).</summary>
    [HttpDelete("{id:int}"), RequirePerm("sub.withdraw")]
    public async Task<IActionResult> Withdraw(int id)
    {
        var s = await data.SubmissionAsync(id);
        if (s.Stage == "Billing") throw AppException.Conflict("ALREADY_BILLING", "Consultant is billing", "End the assignment instead of withdrawing a billing placement.");
        await db.Submissions.Where(x => x.SubmissionId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Stage, "Withdrawn").SetProperty(x => x.StageChangedUtc, DateTime.UtcNow));
        await audit.LogAsync("SUB_WITHDRAW", $"Withdrew {s.ConsultantName} from {s.Title}", "Submission", id);
        return NoContent();
    }

    /// <summary>The onboarding checklist for a submission, with who completed each item and when.</summary>
    [HttpGet("{id:int}/onboarding"), RequirePerm("onboard.view")]
    public async Task<List<OnboardingItemDto>> Onboarding(int id)
    {
        await data.SubmissionAsync(id);
        var done = await db.OnboardingItems.AsNoTracking().Where(o => o.SubmissionId == id)
            .Select(o => new { o.ItemCode, o.IsDone, o.DoneUtc, By = o.DoneBy != null ? o.DoneBy.FullName : null }).ToListAsync();
        return Domain.OnboardingItems.Select(i =>
        {
            var d = done.FirstOrDefault(x => x.ItemCode == i.Code);
            return new OnboardingItemDto(i.Code, i.Label, d?.IsDone == true, d?.IsDone == true ? d.By : null, d?.IsDone == true ? d.DoneUtc : null);
        }).ToList();
    }

    /// <summary>Ticks or unticks one onboarding item. Only allowed from the Offer stage onwards.</summary>
    [HttpPut("{id:int}/onboarding/{code}"), RequirePerm("onboard.edit")]
    public async Task<IActionResult> SetOnboarding(int id, string code, OnboardingInput input)
    {
        if (!Domain.OnboardingItems.Any(i => i.Code == code)) throw AppException.NotFound("Checklist item");
        var s = await data.SubmissionAsync(id);
        if (s.Stage is not ("Offer" or "MSA / PO" or "Billing"))
            throw AppException.Conflict("NOT_IN_ONBOARDING", "Not in onboarding yet", "Move the submission to Offer before working on the onboarding checklist.");

        var item = await db.OnboardingItems.FirstOrDefaultAsync(o => o.SubmissionId == id && o.ItemCode == code);
        if (item is null) { item = new OnboardingItem { SubmissionId = id, ItemCode = code }; db.OnboardingItems.Add(item); }
        item.IsDone = input.Done;
        item.DoneByUserId = me.RequiredId;
        item.DoneUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var label = Domain.OnboardingItems.First(i => i.Code == code).Label;
        await audit.LogAsync("ONBOARDING", $"{(input.Done ? "Completed" : "Reopened")} \"{label}\" for {s.ConsultantName}", "Submission", id);
        return NoContent();
    }
}
