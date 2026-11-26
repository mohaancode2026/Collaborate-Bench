using System.Text.Json;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Services;

/// <summary>
/// The follow-up, task and interview rules of the AI Inbox:
/// <list type="bullet">
/// <item>Follow-ups: 30–60 min after sending details with no reply (same day); every 2 business days after a submission;
/// the business day after an interview, asking for feedback. Every reminder opens a ready draft; nothing is sent automatically.</item>
/// <item>Tasks from incoming mail: a details/documents request → a task plus a draft asking the consultant for what's missing;
/// an interview request → "Interview to schedule"; a vendor reply → a follow-up; a contracts, insurance or immigration question →
/// a task to forward it to that team.</item>
/// <item>Interviews: the slots in the vendor's email become a draft to the consultant; the chosen time is logged with its type;
/// a mock session can be planned. Only the consultant is ever recorded as taking part in an interview.</item>
/// </list>
/// </summary>
public static class InboxWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly string[] InterviewTypes = ["Client call", "Technical call", "L1", "L2", "Final", "Other"];
    public static readonly string[] Teams = ["Contracts", "Insurance", "Immigration"];

    public static List<DateTime> SlotsOf(string? json) => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<DateTime>>(json, Json) ?? [];
    public static List<string> RequestsOf(string? json) => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
    public static string ToJson<T>(T v) => JsonSerializer.Serialize(v, Json);

    // ---------- business days ----------
    public static DateTime AddBusinessDays(DateTime utc, int days)
    {
        var d = utc;
        while (days > 0) { d = d.AddDays(1); if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--; }
        return d;
    }

    // ---------- what an incoming email asks for ----------
    private static readonly (string Item, Regex Re)[] RequestRules =
    [
        ("Updated resume", new(@"\b(updated|latest|word|doc)\s+(copy of\s+)?(the\s+)?resume\b|\bresume\b.{0,20}\b(word|doc)\b", RegexOptions.IgnoreCase)),
        ("RTR", new(@"\bRTR\b|right to represent", RegexOptions.IgnoreCase)),
        ("Visa copy", new(@"\bvisa (copy|copies|proof|status proof)|copy of (the )?visa|\b(H-?1B|GC|EAD|OPT) copy\b|work authori[sz]ation (proof|copy)", RegexOptions.IgnoreCase)),
        ("EAD copy", new(@"\bEAD (card|copy)\b", RegexOptions.IgnoreCase)),
        ("I-94", new(@"\bI-?94\b", RegexOptions.IgnoreCase)),
        ("DL / State ID", new(@"\b(DL|driver'?s? licen[sc]e|state id|photo id)\b", RegexOptions.IgnoreCase)),
        ("Passport copy", new(@"\bpassport (copy|number|no)\b|copy of (the )?passport", RegexOptions.IgnoreCase)),
        ("References", new(@"\breferences?\b(?! to)", RegexOptions.IgnoreCase)),
        ("Skill matrix", new(@"\bskill(s)? matrix\b|\bskill sheet\b", RegexOptions.IgnoreCase)),
        ("LinkedIn profile", new(@"\blinked ?in (url|profile|link)\b", RegexOptions.IgnoreCase)),
        ("Last 4 SSN / DOB", new(@"\blast\s*4\b.{0,10}\bssn\b|\bssn\b.{0,10}\blast\s*4\b|\b(dob|date of birth)\b", RegexOptions.IgnoreCase)),
        ("Current location", new(@"\bcurrent (location|address)\b", RegexOptions.IgnoreCase)),
        ("Interview availability", new(@"\b(interview )?availability\b|\bavailable (times|slots)\b", RegexOptions.IgnoreCase)),
        ("Employer details", new(@"\bemployer (details|name|info)\b|\bH-?1B? employer\b", RegexOptions.IgnoreCase)),
    ];
    private static readonly Regex AskWords = new(@"\b(please|kindly|could you|can you|send|share|provide|need|require|attach|forward)\b", RegexOptions.IgnoreCase);

    /// <summary>The details/documents the vendor asks for (only when the email actually asks).</summary>
    public static List<string> FindRequests(string text)
    {
        var head = text.Length > 2500 ? text[..2500] : text;
        if (!AskWords.IsMatch(head)) return [];
        return RequestRules.Where(r => r.Re.IsMatch(head)).Select(r => r.Item).Distinct().Take(8).ToList();
    }

    private static readonly (string Team, Regex Re)[] TeamRules =
    [
        ("Insurance", new(@"\bCOI\b|certificate of insurance|(general |professional |cyber )?liability insurance|\binsurance (certificate|coverage|requirement)|\bE&O\b|workers'? comp", RegexOptions.IgnoreCase)),
        ("Immigration", new(@"\bLCA\b|\bI-?129\b|\bH-?1B (transfer|amendment|extension|petition)|\bpetition\b|\bI-?140\b|\bPERM\b|\bE-?Verify\b|\bI-?9\b form|immigration (question|status|attorney)|\bRFE\b", RegexOptions.IgnoreCase)),
        ("Contracts", new(@"\bMSA\b|\bSOW\b|\bNDA\b|\bW-?9\b|purchase order|\bPO\b (number|copy)|sub-?contract|vendor agreement|signed (agreement|contract)|contract (terms|copy|signature)|\bnet ?\d{2}\b|payment terms|\binvoic", RegexOptions.IgnoreCase)),
    ];

    /// <summary>Contracts, Insurance or Immigration when the email is a question for that team.</summary>
    public static string? FindTeam(string text)
    {
        var head = text.Length > 3000 ? text[..3000] : text;
        return TeamRules.FirstOrDefault(r => r.Re.IsMatch(head)).Team;
    }

    // ---------- interview slots ----------
    private static readonly Regex DateRe = new(
        @"(?<!\d)(?<m>\d{1,2})/(?<d>\d{1,2})(?:/(?<y>\d{2,4}))?(?!\d)|\b(?<mn>jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+(?<d2>\d{1,2})(?:st|nd|rd|th)?(?:,?\s*(?<y2>\d{4}))?",
        RegexOptions.IgnoreCase);
    private static readonly Regex TimeRe = new(@"(?<h>\d{1,2})(?::(?<min>\d{2}))?\s*(?<ap>[AaPp]\.?[Mm]\.?)", RegexOptions.IgnoreCase);
    private static readonly Regex TzRe = new(@"\b(?<tz>EST|EDT|ET|CST|CDT|CT|MST|MDT|MT|PST|PDT|PT|IST)\b");
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static TimeZoneInfo Zone(string? tz)
    {
        var id = tz switch
        {
            "CST" or "CDT" or "CT" => "America/Chicago", "MST" or "MDT" or "MT" => "America/Denver",
            "PST" or "PDT" or "PT" => "America/Los_Angeles", "IST" => "Asia/Kolkata", _ => "America/New_York",
        };
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return TimeZoneInfo.Utc; }
    }

    public static TimeZoneInfo Eastern => Zone("ET");

    /// <summary>Up to 3 interview slots written in the email ("11/26 10:00 AM EST", "Nov 27 at 2 PM CST"). US Eastern when no zone is given.</summary>
    public static List<DateTime> FindSlots(string text, DateTime receivedUtc)
    {
        var head = text.Length > 3000 ? text[..3000] : text;
        var slots = new List<DateTime>();
        foreach (Match d in DateRe.Matches(head))
        {
            var after = head.Substring(d.Index + d.Length, Math.Min(45, head.Length - d.Index - d.Length));
            var t = TimeRe.Match(after);
            if (!t.Success) continue;
            var rest = after[(t.Index + t.Length)..] + head.Substring(Math.Min(head.Length, d.Index + d.Length + 45), Math.Min(30, Math.Max(0, head.Length - d.Index - d.Length - 45)));
            var tz = TzRe.Match(rest) is { Success: true } z ? z.Groups["tz"].Value : null;
            int month, day, year;
            if (d.Groups["m"].Success) { month = int.Parse(d.Groups["m"].Value); day = int.Parse(d.Groups["d"].Value); year = d.Groups["y"].Success ? int.Parse(d.Groups["y"].Value) : 0; }
            else { month = Array.FindIndex(Months, x => d.Groups["mn"].Value.ToLowerInvariant().StartsWith(x)) + 1; day = int.Parse(d.Groups["d2"].Value); year = d.Groups["y2"].Success ? int.Parse(d.Groups["y2"].Value) : 0; }
            if (year is > 0 and < 100) year += 2000;
            if (year == 0) year = receivedUtc.Year;
            var hour = int.Parse(t.Groups["h"].Value) % 12 + (t.Groups["ap"].Value.StartsWith('p') || t.Groups["ap"].Value.StartsWith('P') ? 12 : 0);
            var minute = t.Groups["min"].Success ? int.Parse(t.Groups["min"].Value) : 0;
            if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59) continue;
            DateTime local;
            try { local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified); } catch { continue; }
            if (!d.Groups["y"].Success && !d.Groups["y2"].Success && local < receivedUtc.AddDays(-30)) local = local.AddYears(1);   // "1/5" in December = next year
            var utc = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, Zone(tz)), DateTimeKind.Utc);
            if (utc < receivedUtc.AddHours(-12) || slots.Contains(utc)) continue;
            slots.Add(utc);
            if (slots.Count == 3) break;
        }
        return slots;
    }

    /// <summary>"Tue, Nov 26, 10:00 AM ET" (US Eastern), for drafts.</summary>
    public static string Et(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern).ToString("ddd, MMM d, h:mm tt") + " ET";

    // ---------- who the email is about ----------
    public static int? FindConsultant(string text, IEnumerable<ConsultantDto> bench) =>
        bench.Where(c => c.Name.Trim().Contains(' ')).OrderByDescending(c => c.Name.Length)
            .FirstOrDefault(c => Regex.IsMatch(text, $@"\b{Regex.Escape(c.Name.Trim())}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))?.Id;

    // ---------- the bench a mailbox is matched against ----------
    /// <summary>Available consultants for the user: their own and their team's, or the whole bench for roles that see it all.
    /// Skills include those found in the consultant's latest resume, so matching is "resume based".</summary>
    public static async Task<List<ConsultantDto>> BenchForAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var role = await db.Users.AsNoTracking().Where(u => u.UserId == userId).Select(u => new { u.RoleId, u.Role.DataScope }).FirstAsync(ct);
        var wholeBench = role.DataScope == "ALL" || await db.RolePermissions.AnyAsync(rp => rp.RoleId == role.RoleId && rp.PermissionCode == "bench.assign", ct);
        var q = db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(c => c.Status == "Bench" || c.Status == "Marketing");
        if (!wholeBench)
        {
            var owners = await db.UserHierarchy.Where(h => h.AncestorUserId == userId).Select(h => h.DescendantUserId).ToListAsync(ct);
            if (!owners.Contains(userId)) owners.Add(userId);
            q = q.Where(c => owners.Contains(c.OwnerUserId));
        }
        var bench = await q.Select(c => new ConsultantDto
        {
            Id = c.ConsultantId, Name = c.FullName, Title = c.Title ?? "", Email = c.Email ?? "", Visa = c.VisaStatus ?? "",
            Location = c.Location ?? "", Relocate = c.OpenToRelocate, Rate = c.PayRate, Exp = c.ExperienceYears ?? 0,
            Status = c.Status, Skills = c.Skills, OwnerId = c.OwnerUserId, OwnerName = c.Owner.FullName,
        }).ToListAsync(ct);
        var ids = bench.Select(c => c.Id).ToList();
        var resumes = await db.Resumes.IgnoreQueryFilters().AsNoTracking().Where(r => ids.Contains(r.ConsultantId) && r.ExtractedText != "")
            .GroupBy(r => r.ConsultantId).Select(g => g.OrderByDescending(r => r.UploadedUtc).Select(r => new { r.ConsultantId, r.ExtractedText }).First())
            .ToListAsync(ct);
        var byId = resumes.ToDictionary(r => r.ConsultantId, r => r.ExtractedText);
        foreach (var c in bench)
            if (byId.TryGetValue(c.Id, out var text) && !string.IsNullOrWhiteSpace(text))
                c.Skills = c.Skills.Concat(Rules.FindSkills(text)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return bench;
    }

    /// <summary>"Green Card", "US Citizen", "H-1B", "usc" … → the app's codes GC / USC / H1B; any other value as it is ("GC-EAD", "OPT").</summary>
    public static string NormVisa(string? v)
    {
        var raw = (v ?? "").Trim();
        return Regex.Replace(raw.ToUpperInvariant(), @"[\s.\-]", "") switch
        {
            "GREENCARD" or "GC" or "PERMANENTRESIDENT" or "LPR" => "GC",
            "USC" or "USCITIZEN" or "CITIZEN" or "USCITIZENSHIP" or "US" => "USC",
            "H1" or "H1B" => "H1B",
            _ => raw,
        };
    }

    /// <summary>A job that only takes green-card holders or US citizens (every visa it names is GC or USC).</summary>
    public static bool GcUscOnly(List<string> visas) =>
        visas.Count > 0 && visas.All(v => NormVisa(v) is "GC" or "USC");

    /// <summary>
    /// Ranks the profiles that fit a job (the executive's own assigned profiles: the caller passes that bench). Order:
    /// visa (H-1B first, then other accepted visas, then unknown, then not accepted), skill match %, years of experience
    /// (more first), rate (lower first, unknown last), location fit. A GC/USC-only job shows only GC and USC profiles.
    /// Up to 10, at or above <paramref name="minScore"/> overall; strong ones flagged "resume ready".
    /// </summary>
    public static List<InboxMatch> Match(InboxJob job, List<ConsultantDto> bench, int minScore, int readyScore)
    {
        var req = new RequirementDto
        {
            Title = job.Title, Client = job.Client ?? "", Location = job.Location ?? "", Mode = job.Mode, Type = job.Type,
            Rate = job.Rate, Skills = job.Skills, Visas = job.Visas.Count > 0 ? job.Visas.Select(NormVisa).Distinct().ToList() : ["Any"],
        };
        var gcUscOnly = GcUscOnly(job.Visas);
        int VisaRank(ConsultantDto c, MatchResult m) =>
            string.IsNullOrWhiteSpace(c.Visa) ? 2 : !m.VisaOk ? 3 : NormVisa(c.Visa) == "H1B" ? 0 : 1;
        return bench
            .Where(c => !gcUscOnly || NormVisa(c.Visa) is "GC" or "USC")
            .Select(c => (c, m: Matching.Score(c, req), loc: Matching.LocationFit(c, req)))
            .Select(x => (x.c, x.m, x.loc, pct: job.Skills.Count == 0 ? 0 : (int)Math.Round(100.0 * x.m.Hit.Count / job.Skills.Count)))
            .Where(x => x.m.Score >= minScore)
            .OrderBy(x => VisaRank(x.c, x.m))
            .ThenByDescending(x => x.pct)
            .ThenByDescending(x => x.c.Exp)
            .ThenBy(x => x.c.Rate > 0 ? x.c.Rate : decimal.MaxValue)
            .ThenByDescending(x => x.loc)
            .Take(10)
            .Select(x => new InboxMatch(x.c.Id, x.c.Name, x.c.Title, x.m.Score, x.m.Why, x.c.Exp,
                x.m.Score >= readyScore && x.m.VisaOk && (job.MinYears is null || x.c.Exp >= job.MinYears),
                string.IsNullOrWhiteSpace(x.c.Visa) ? null : x.c.Visa, x.pct, x.c.Rate, string.IsNullOrWhiteSpace(x.c.Location) ? null : x.c.Location,
                x.m.Hit, x.m.Miss))
            .ToList();
    }

    // ---------- tasks ----------
    /// <summary>The tasks an incoming email calls for.</summary>
    /// <param name="vendorReply">It answers something we sent (our email is earlier in the same thread).</param>
    public static List<InboxTask> IncomingTasks(InboxItem i, bool vendorReply, string? consultantName, string? interviewType)
    {
        var who = i.FromName ?? i.FromAddress;
        var tasks = new List<InboxTask>();
        var requests = RequestsOf(i.RequestsJson);
        if (requests.Count > 0)
            tasks.Add(new InboxTask
            {
                Kind = "Task", Auto = true, Action = "AskConsultant", ConsultantId = i.ConsultantId, DueUtc = DateTime.UtcNow.AddHours(2),
                Title = Cut($"Collect from {consultantName ?? "the consultant"}: {string.Join(", ", requests)}", 300),
            });
        if (i.Team is { } team)
            tasks.Add(new InboxTask { Kind = "Task", Auto = true, Action = "Forward:" + team, DueUtc = DateTime.UtcNow.AddHours(4), Title = Cut($"Forward to the {team} team: {i.Subject}", 300) });
        if (i.Category == "Interview")
        {
            var slots = SlotsOf(i.SlotsJson);
            tasks.Add(new InboxTask
            {
                Kind = "Interview", Auto = true, Action = "Slots", ConsultantId = i.ConsultantId, SlotsJson = slots.Count > 0 ? i.SlotsJson : null,
                InterviewType = interviewType, DurationMinutes = 30,
                Title = Cut($"Interview to schedule{(consultantName is null ? "" : $" for {consultantName}")}: {i.Subject}", 300),
                Notes = slots.Count > 0 ? $"Slots offered: {string.Join("; ", slots.Select(Et))}" : null,
            });
        }
        else if (requests.Count == 0 && i.Team is null && i.Category != "Noise" && (vendorReply || i.Category is "FollowUp" or "Rate"))
            tasks.Add(new InboxTask { Kind = "FollowUp", Auto = true, Action = "Reply", ConsultantId = i.ConsultantId, DueUtc = DateTime.UtcNow.AddHours(2), Title = Cut($"Reply to {who}: {i.Subject}", 300) });
        return tasks;
    }

    private static readonly Regex SubmissionRe = new(@"\b(submission|submitting|profile|resume|candidate)\b", RegexOptions.IgnoreCase);

    /// <summary>Follow-ups for something we sent to a vendor. <paramref name="firstInThread"/>: no earlier email from us in the thread.</summary>
    public static List<InboxTask> SentTasks(InboxItem o, bool firstInThread, int earlierSubmissionReminders, string? consultantName, int maxRepeats)
    {
        var tasks = new List<InboxTask>();
        var age = DateTime.UtcNow - o.ReceivedUtc;
        var submission = SubmissionRe.IsMatch(o.Subject) || InboxTriage.AttachmentsOf(o.AttachmentsJson).Any(a => Regex.IsMatch(a.Name, @"\.(pdf|docx?)$", RegexOptions.IgnoreCase));
        var to = (o.ToAddresses ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "the vendor";
        // Same-day check on details we sent: 45 minutes later, if they haven't answered.
        if (firstInThread && age < TimeSpan.FromHours(20))
            tasks.Add(new InboxTask { Kind = "FollowUp", Auto = true, Action = "Nudge", ConsultantId = o.ConsultantId, DueUtc = o.ReceivedUtc.AddMinutes(45), Title = Cut($"No reply yet from {to}: {o.Subject}", 300) });
        // After a submission: every 2 business days until they answer (each follow-up we send restarts the clock).
        if (submission && age < TimeSpan.FromDays(14) && earlierSubmissionReminders < maxRepeats)
            tasks.Add(new InboxTask
            {
                Kind = "FollowUp", Auto = true, Action = "Nudge", ConsultantId = o.ConsultantId, RepeatBusinessDays = 2, Sequence = earlierSubmissionReminders + 1,
                DueUtc = AddBusinessDays(o.ReceivedUtc, 2),
                Title = Cut($"Follow up on {(consultantName is null ? "the submission" : consultantName + "'s submission")} ({earlierSubmissionReminders + 1}): {o.Subject}", 300),
            });
        return tasks;
    }

    /// <summary>Keeps an interview's companion tasks in step with it: the feedback reminder (business day after) and the mock session.</summary>
    public static async Task SyncInterviewAsync(AppDbContext db, InboxTask t, string? consultantName, CancellationToken ct)
    {
        if (t.Kind != "Interview") return;
        var kids = await db.InboxTasks.Where(x => x.ParentTaskId == t.InboxTaskId && (x.Action == "Feedback" || x.Action == "Mock")).ToListAsync(ct);
        void Upsert(string action, bool want, DateTime? due, string title)
        {
            var k = kids.FirstOrDefault(x => x.Action == action);
            if (!want) { if (k is { Status: "Open" }) { k.Status = "Dismissed"; k.ClosedUtc = DateTime.UtcNow; } return; }
            if (k is null) db.InboxTasks.Add(k = new InboxTask { UserId = t.UserId, InboxItemId = t.InboxItemId, ParentTaskId = t.InboxTaskId, Auto = true, Action = action });
            if (k.Status != "Open") { k.Status = "Open"; k.ClosedUtc = null; }
            k.Kind = action == "Feedback" ? "FollowUp" : "Task"; k.DueUtc = due; k.ConsultantId = t.ConsultantId; k.Title = Cut(title, 300);
        }
        var who = consultantName ?? "the consultant";
        var open = t.Status == "Open" && t.DueUtc is not null;
        Upsert("Feedback", t.DueUtc is not null && t.Status != "Dismissed", t.DueUtc is { } d ? AddBusinessDays(d, 1) : null,
            $"Ask for {who}'s {t.InterviewType ?? "interview"} feedback ({(t.DueUtc is { } dd ? Et(dd) : "")})");
        Upsert("Mock", t.MockSession && open, t.DueUtc is { } m ? (AddBusinessDays(m.AddDays(-2), 1) is var x && x < m ? x : m.AddHours(-3)) : null,
            $"Mock interview session with {who} before the {t.InterviewType ?? "interview"}");
        await db.SaveChangesAsync(ct);
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}
