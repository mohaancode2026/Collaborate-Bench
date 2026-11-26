using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;

namespace CollaborateBench.Api.Services;

/// <summary>Writes the audit trail through its own DbContext so it never saves a controller's half-finished changes.</summary>
public sealed class AuditService(IDbContextFactory<AppDbContext> factory, ICurrentUser me, IHttpContextAccessor http, ILogger<AuditService> log)
{
    public async Task LogAsync(string action, string detail, string? entityType = null, int? entityId = null, string outcome = "Success", int? userId = null)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.AuditLogs.Add(new AuditLog
            {
                UserId = userId ?? me.Id, Action = action, EntityType = entityType, EntityId = entityId, Outcome = outcome,
                Detail = detail.Length > 1000 ? detail[..1000] : detail,
                IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Auditing must never break the user's action, but it must be visible to ops.
            log.LogError(ex, "Audit write failed: {Action} {Detail}", action, detail);
        }
    }

    public Task DeniedAsync(string detail) => LogAsync("ACCESS_DENIED", detail, outcome: "Denied");
}

/// <summary>Explainable fit score: skills 60%, rate 18%, visa 12%, location 10%.</summary>
public static class Matching
{
    private static string? State(string loc)
    {
        var m = System.Text.RegularExpressions.Regex.Match(loc ?? "", @",\s?([A-Z]{2})\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static MatchResult Score(ConsultantDto c, RequirementDto j, string? submittedStage = null)
    {
        var cs = new HashSet<string>(c.Skills, StringComparer.OrdinalIgnoreCase);
        var hit = j.Skills.Where(cs.Contains).ToList();
        var miss = j.Skills.Where(s => !cs.Contains(s)).ToList();
        var skill = j.Skills.Count == 0 ? 0 : (double)hit.Count / j.Skills.Count;

        var target = (double)j.Rate * 0.88;
        var pay = (double)c.Rate;
        // No posted bill rate (common for jobs from outside job boards): neutral, neither rewarded nor penalised.
        var rate = pay <= 0 || j.Rate <= 0 ? 0.7 : pay <= target ? 1 : Math.Max(0, 1 - (pay - target) / target * 2.5);

        var visas = j.Visas.Count == 0 ? new List<string> { "Any" } : j.Visas;
        // A sourced lead's visa often isn't known yet: don't penalise it, but say so in the explanation.
        var visaUnknown = string.IsNullOrEmpty(c.Visa);
        var visaOk = visaUnknown || visas.Contains("Any", StringComparer.OrdinalIgnoreCase) || visas.Contains(c.Visa, StringComparer.OrdinalIgnoreCase)
                     || (c.Visa is "USC" or "GC" && visas.Any(v => v is "USC" or "GC"));

        var loc = LocationFit(c, j);

        var total = (skill * 0.6 + rate * 0.18 + (visaOk ? 1 : 0) * 0.12 + loc * 0.10) * 100 * (visaOk ? 1 : 0.6);
        var why = new List<string> { hit.Count > 0 ? $"Matches {hit.Count}/{j.Skills.Count} skills ({string.Join(", ", hit)})" : "No direct skill overlap" };
        if (miss.Count > 0) why.Add("gaps: " + string.Join(", ", miss));
        why.Add(j.Rate <= 0 ? "bill rate not posted" : pay <= 0 ? "rate not set" : pay <= target ? $"rate ${c.Rate:0}/hr fits the ${j.Rate:0}/hr bill" : $"rate ${c.Rate:0}/hr is tight against ${j.Rate:0}/hr bill");
        why.Add(visaUnknown ? "visa not known yet, confirm with the candidate" : visaOk ? $"{c.Visa} accepted" : $"{c.Visa} not in client visa list");
        if (loc < 1) why.Add(c.Relocate ? "open to relocate" : "location mismatch");

        return new MatchResult(c.Id, c.Name, c.Title, c.Visa, c.Rate, c.OwnerName, j.Id, j.Title, j.Client, j.Vendor, j.Type, j.Rate,
            (int)Math.Min(100, Math.Round(total)), hit, miss, string.Join(" · ", why), visaOk, submittedStage);
    }

    /// <summary>1 = remote job or same state, 0.75 = open to relocate, 0.35 = elsewhere.</summary>
    public static double LocationFit(ConsultantDto c, RequirementDto j) =>
        j.Mode == "Remote" || j.Location == "Remote" ? 1
        : State(c.Location) is { } s && s == State(j.Location) ? 1
        : c.Relocate ? 0.75 : 0.35;

    public static decimal SuggestRate(decimal pay, decimal bill)
    {
        var floor = Math.Ceiling(pay * 1.10m);
        var ceil = Math.Floor(bill * 0.95m);
        var target = Math.Round((pay > 0 ? pay : bill * 0.8m) * 1.12m);
        return Math.Max(Math.Min(ceil, target), Math.Min(floor, ceil));
    }

    /// <summary>A submit rate at least the pay rate and at most the bill rate: above pay when there's room, equal to pay
    /// (no margin) when bill equals pay, null when bill is below pay. Never returns a rate the submission rules reject.</summary>
    public static decimal? TrySuggestRate(decimal pay, decimal bill)
    {
        if (pay <= 0 || bill < pay) return null;
        if (bill == pay) return pay;
        var s = SuggestRate(pay, bill);
        if (s > pay && s <= bill) return s;
        var mid = Math.Floor((pay + bill) / 2);   // very thin margin: halfway, still inside the rules
        return mid > pay && mid <= bill ? mid : bill;
    }

    /// <summary>Why no submit rate fits, in words a recruiter can act on.</summary>
    public static string NoMarginMessage(decimal pay, decimal bill, bool fromJobPosting) => fromJobPosting
        ? $"The job posting shows ${bill:0.##}/hr, below the consultant's ${pay:0.##}/hr pay rate. Job boards usually show what the employer pays, not the vendor's bill rate: get the bill rate from the vendor and enter it."
        : $"The ${bill:0.##}/hr bill rate is below the consultant's ${pay:0.##}/hr pay rate. Ask the vendor for a higher bill rate or agree a lower pay rate first.";
}

/// <summary>Rules that drive the dashboard risks and the AI briefing.</summary>
public static class Insights
{
    public static List<Insight> Build(List<ConsultantDto> consultants, List<SubmissionDto> subs, List<RequirementDto> open, bool canSeeMargin)
    {
        var list = new List<Insight>();
        var bench = consultants.Where(c => c.Status is "Bench" or "Marketing").ToList();

        var idle = bench.Where(c => c.ActiveSubmissions == 0).OrderByDescending(c => c.DaysOnBench).ToList();
        if (idle.Count > 0)
            list.Add(new("risk", $"{idle.Count} consultant{(idle.Count > 1 ? "s have" : " has")} no active submission",
                string.Join(", ", idle.Take(4).Select(c => $"{c.Name} ({c.DaysOnBench}d on bench)")), "Find matches", "/match"));

        var stuck = subs.Where(s => s.Stage != "Billing" && s.DaysInStage >= 3).ToList();
        if (stuck.Count > 0)
            list.Add(new("risk", $"{stuck.Count} submission{(stuck.Count > 1 ? "s are" : " is")} stuck for 3+ days",
                string.Join(", ", stuck.Select(s => $"{s.ConsultantName} at {s.Stage} ({s.DaysInStage}d)")) + ". Follow up with the vendor.", "Open pipeline", "/pipeline"));

        var submitted = subs.Select(s => (s.ConsultantId, s.RequirementId)).ToHashSet();
        var hot = bench.SelectMany(c => open.Where(j => !submitted.Contains((c.Id, j.Id))).Select(j => Matching.Score(c, j)))
            .Where(m => m.Score >= 85).OrderByDescending(m => m.Score).ToList();
        if (hot.Count > 0)
            list.Add(new("opportunity", $"{hot.Count} strong match{(hot.Count > 1 ? "es" : "")} (85%+) not yet submitted",
                string.Join(" · ", hot.Take(3).Select(m => $"{m.ConsultantName} → {m.RequirementTitle} ({m.Score}%)")), "Review matches", "/match"));

        var offers = subs.Where(s => s.Stage is "Offer" or "MSA / PO").ToList();
        if (offers.Count > 0)
            list.Add(new("info", $"{offers.Count} placement{(offers.Count > 1 ? "s" : "")} in offer / onboarding",
                string.Join(", ", offers.Select(s => $"{s.ConsultantName} ({s.Client})")) + ". Close out MSA/PO and compliance.", "Onboarding", "/onboarding"));

        if (canSeeMargin)
        {
            var low = subs.Where(s => s.SubmitRate > 0 && (s.SubmitRate - s.PayRate) / s.SubmitRate < 0.08m).ToList();
            if (low.Count > 0)
                list.Add(new("risk", $"{low.Count} submission{(low.Count > 1 ? "s" : "")} below 8% margin",
                    string.Join(", ", low.Select(s => $"{s.ConsultantName}: ${s.SubmitRate - s.PayRate:0.##}/hr")), "Rate calculator", "/rates"));
        }
        return list;
    }
}
