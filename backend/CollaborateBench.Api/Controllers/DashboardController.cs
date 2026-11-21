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

/// <summary>Dashboard numbers, insights and the recruiter-wise report (with Excel export), all limited to the caller's view.</summary>
[ApiController, Route("api/dashboard")]
public sealed class DashboardController(DataService data, ICurrentUser me, AuditService audit) : ControllerBase
{
    /// <summary>KPIs, stage counts, risk/opportunity insights, top matches, recent activity and the recruiter table.</summary>
    [HttpGet, RequirePerm("dash.view")]
    public async Task<DashboardDto> Get([FromQuery] string? scope)
    {
        var cons = await data.ConsultantsAsync(scope);
        var subs = await data.SubmissionsAsync(scope);
        var reqs = await data.RequirementsAsync(true);
        var bench = cons.Where(c => c.Status is "Bench" or "Marketing").ToList();
        var done = subs.Select(s => (s.ConsultantId, s.RequirementId)).ToHashSet();
        var top = bench.SelectMany(c => reqs.Where(r => !done.Contains((c.Id, r.Id))).Select(r => Matching.Score(c, r)))
            .OrderByDescending(m => m.Score).Take(5).ToList();
        var billing = subs.Where(s => s.Stage == "Billing").ToList();
        return new DashboardDto(
            bench.Count, bench.Count(c => c.Status == "Marketing"), reqs.Count, reqs.Count(r => r.DaysOld == 0),
            subs.Count(s => s.Stage != "Billing"), subs.Count(s => s.Stage is "Client Interview" or "Offer"), billing.Count,
            me.Has("margin.view") ? billing.Sum(s => (s.SubmitRate - s.PayRate) * 40) : null,
            Domain.Stages.Select(st => new StageCount(st, subs.Count(s => s.Stage == st))).ToList(),
            Insights.Build(cons, subs, reqs, me.Has("margin.view")), top,
            await data.ActivityAsync(6, "Success", false),
            await ByRecruiterAsync(scope, bench, subs, null, null));
    }

    /// <summary>Recruiter-wise report for a date range. <paramref name="from"/> is inclusive, <paramref name="to"/> exclusive;
    /// the browser sends its local midnights so "today" means the user's day.</summary>
    [HttpGet("recruiters"), RequirePerm("dash.view")]
    public async Task<List<RecruiterStat>> Recruiters([FromQuery] string? scope, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to) =>
        (await LoadReportAsync(scope, from, to)).Rows;

    /// <summary>The same report as an Excel workbook (summary + submission detail).
    /// <paramref name="range"/> is the label shown in the app; <paramref name="tzOffset"/> is the browser's getTimezoneOffset() in minutes.</summary>
    [HttpGet("recruiters/export"), RequirePerm("dash.view")]
    public async Task<IActionResult> ExportRecruiters([FromQuery] string? scope, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? range, [FromQuery] int? tzOffset)
    {
        if (me.DataScope == "OWN" || string.Equals(scope, "mine", StringComparison.OrdinalIgnoreCase))
            throw AppException.Forbidden("The recruiter report is for Team Leads and above, and needs a team or all-teams view.");
        var (rows, subs) = await LoadReportAsync(scope, from, to);
        var offset = TimeSpan.FromMinutes(-Math.Clamp(tzOffset ?? 0, -14 * 60, 14 * 60));
        var label = string.IsNullOrWhiteSpace(range) ? (from is null && to is null ? "All time" : "Custom range") : range.Trim();
        if (label.Length > 80) label = label[..80];
        var scopeLabel = (scope ?? "").ToLowerInvariant() switch { "team" => "My team", _ => me.DataScope == "TEAM" ? "My team" : "All teams" };

        var bytes = RecruiterReportExcel.Build(rows, subs, label, scopeLabel, me.Name, offset, me.Has("margin.view"));
        await audit.LogAsync("REPORT_EXPORT", $"Exported the recruiter report to Excel ({label}: {rows.Count} recruiters, {subs.Count} submissions)");

        string Day(DateTimeOffset d) => (d.UtcDateTime + offset).ToString("yyyy-MM-dd");
        var name = from is { } f && to is { } t ? $"recruiter-report_{Day(f)}_to_{Day(t.AddDays(-1))}.xlsx"
                 : $"recruiter-report_all-time_{(DateTime.UtcNow + offset):yyyy-MM-dd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    /// <summary>Validated range → recruiter rows plus the submissions behind them (same scope and period).</summary>
    private async Task<(List<RecruiterStat> Rows, List<SubmissionDto> Subs)> LoadReportAsync(string? scope, DateTimeOffset? from, DateTimeOffset? to)
    {
        new Validator()
            .Check(from is null || to is null || from < to, "to", "The end date must be on or after the start date")
            .Check(from is null || from > DateTimeOffset.UtcNow.AddYears(-20), "from", "Choose a start date within the last 20 years")
            .ThrowIfInvalid();
        var cons = await data.ConsultantsAsync(scope);
        var subs = InRange(await data.SubmissionsAsync(scope), from?.UtcDateTime, to?.UtcDateTime);
        var bench = cons.Where(c => c.Status is "Bench" or "Marketing").ToList();
        var rows = await ByRecruiterAsync(scope, bench, subs, null, null);
        var ids = rows.Select(r => r.UserId).ToHashSet();
        return (rows, subs.Where(s => ids.Contains(s.OwnerId)).ToList());
    }

    private static List<SubmissionDto> InRange(List<SubmissionDto> subs, DateTime? fromUtc, DateTime? toUtc) =>
        subs.Where(x => (fromUtc is null || x.CreatedUtc >= fromUtc) && (toUtc is null || x.CreatedUtc < toUtc)).ToList();

    /// <summary>Recruiter-wise breakdown for Admins, Delivery Managers and Team Leads. Built from the already-scoped lists,
    /// so nobody sees counts outside their team. Empty for Recruiters and the "My records" view.</summary>
    private async Task<List<RecruiterStat>> ByRecruiterAsync(string? scope, List<ConsultantDto> bench, List<SubmissionDto> subs,
        DateTime? fromUtc, DateTime? toUtc)
    {
        if (me.DataScope == "OWN" || string.Equals(scope, "mine", StringComparison.OrdinalIgnoreCase)) return [];
        var margin = me.Has("margin.view");
        subs = InRange(subs, fromUtc, toUtc);
        var users = await data.ScopeUsersAsync(scope);
        return users
            .Select(u =>
            {
                var b = bench.Where(c => c.OwnerId == u.Id).ToList();
                var s = subs.Where(x => x.OwnerId == u.Id).ToList();
                var billing = s.Where(x => x.Stage == "Billing").ToList();
                return new RecruiterStat(u.Id, u.Name, u.Role, u.Team,
                    b.Count, b.Count(c => c.Status == "Marketing"),
                    Domain.Stages.Select(st => new StageCount(st, s.Count(x => x.Stage == st))).ToList(),
                    s.Count, s.Count - billing.Count, billing.Count,
                    s.Count(x => x.Stage != "Billing" && x.DaysInStage >= 3),
                    margin ? billing.Sum(x => (x.SubmitRate - x.PayRate) * 40) : null,
                    s.Count > 0 ? s.Max(x => x.CreatedUtc) : null);
            })
            // Recruiters and Team Leads always show (an empty row is useful to spot); managers only when they own work.
            .Where(r => r.Role is "Recruiter" or "Team Lead" || r.Bench > 0 || r.Active + r.Placed > 0)
            .OrderByDescending(r => r.Active).ThenByDescending(r => r.Placed).ThenBy(r => r.Name)
            .ToList();
    }
}
