using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Services;

/// <summary>Creates submissions. Used by the Submissions API and by "apply candidate" on external jobs, so both follow the same rules.</summary>
public sealed class SubmissionService(AppDbContext db, DataService data, ICurrentUser me, AuditService audit)
{
    /// <summary>RTR, rate and notes rules. Callers that create a requirement first run this before creating anything.</summary>
    public static Validator Validate(Validator v, bool rtrSigned, decimal submitRate, decimal payRate, decimal billRate, string? notes) => v
        .Check(rtrSigned, "rtrSigned", "An RTR (Right to Represent) is required before submitting")
        .Check(submitRate > 0, "submitRate", "Enter the submit rate")
        // Bill below pay: no submit rate can work, so say that once instead of two contradicting errors.
        // Submitting at the pay rate (no margin) is allowed; the screens warn about it.
        .Check(billRate <= 0 || payRate <= 0 || billRate >= payRate, "billRate", Matching.NoMarginMessage(payRate, billRate, fromJobPosting: false))
        .Check(billRate <= 0 || payRate > billRate || submitRate <= billRate, "submitRate", $"Can't exceed the ${billRate:0}/hr bill rate")
        .Check(payRate > 0, "payRate", "Enter the consultant's pay rate")
        .Check(submitRate >= payRate, "submitRate", "Can't be below the consultant pay rate")
        .Check((notes?.Length ?? 0) <= 1000, "notes", "Keep notes under 1,000 characters");

    public static void EnsureAvailable(ConsultantDto c)
    {
        if (c.Status is "Placed" or "Inactive")
            throw AppException.Conflict("CONSULTANT_UNAVAILABLE", "Consultant not available", $"{c.Name} is {c.Status.ToLowerInvariant()} and can't be submitted.");
    }

    private static AppException Duplicate(ConsultantDto c, RequirementDto r) => AppException.Conflict("DUPLICATE_SUBMISSION", "Already submitted",
        $"{c.Name} has already been submitted to \"{r.Title}\". Duplicate submissions break the RTR with the vendor.");

    public async Task<(SubmissionDto Submission, List<string> Warnings)> CreateAsync(SubmissionInput input, string auditSuffix = "")
    {
        var c = await data.ConsultantAsync(input.ConsultantId);   // team filter: must be in your view
        var r = await data.RequirementAsync(input.RequirementId);
        Validate(new Validator(), input.RtrSigned, input.SubmitRate, input.PayRate, r.Rate, input.Notes).ThrowIfInvalid();
        if (r.Status != "Open") throw AppException.Conflict("REQUIREMENT_CLOSED", "Requirement is closed", $"\"{r.Title}\" is no longer accepting submissions.");
        EnsureAvailable(c);

        // Checked across all teams (the unique index is the final guard against two clicks at once).
        if (await db.Submissions.IgnoreQueryFilters().AnyAsync(s => s.ConsultantId == c.Id && s.RequirementId == r.Id))
            throw Duplicate(c, r);

        var m = Matching.Score(c, r);
        var sub = new Submission
        {
            ConsultantId = c.Id, RequirementId = r.Id, OwnerUserId = c.OwnerId, SubmitRate = input.SubmitRate, PayRate = input.PayRate,
            MatchScore = (byte)m.Score, RtrSigned = true, Notes = input.Notes?.Trim(),
        };
        db.Submissions.Add(sub);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e))
        {
            db.Entry(sub).State = EntityState.Detached;
            throw Duplicate(c, r);
        }
        db.SubmissionStageHistory.Add(new SubmissionStageHistory { SubmissionId = sub.SubmissionId, ToStage = "Submitted", ChangedByUserId = me.RequiredId });
        await db.SaveChangesAsync();
        await db.Consultants.Where(x => x.ConsultantId == c.Id && x.Status == "Bench").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Marketing"));
        await audit.LogAsync("SUB_CREATE", $"Submitted {c.Name} to {r.Client} ({r.Title}) with RTR{auditSuffix}", "Submission", sub.SubmissionId);

        var warnings = new List<string>();
        if (!m.VisaOk) warnings.Add($"{c.Visa} isn't in the client's visa list. The vendor may reject this.");
        if (m.Score < 50) warnings.Add($"Low match score ({m.Score}%). Missing: {string.Join(", ", m.Miss)}.");
        return (await data.SubmissionAsync(sub.SubmissionId), warnings);
    }
}
