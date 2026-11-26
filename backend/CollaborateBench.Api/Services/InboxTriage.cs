using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Services;

/// <summary>"Inbox" section of appsettings (AI Inbox).</summary>
public sealed class InboxOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>How far back the first read of a mailbox goes.</summary>
    public int FirstSyncDays { get; set; } = 3;
    /// <summary>Most emails triaged per mailbox per sync (each one may be an AI call).</summary>
    public int MaxPerSync { get; set; } = 40;
    /// <summary>Lowest fit score listed as a match for a job email.</summary>
    public int MatchMinScore { get; set; } = 55;
    /// <summary>Score from which a match (with enough experience) is "resume ready": pushed to the resume tailor.</summary>
    public int ResumeReadyScore { get; set; } = 70;
    /// <summary>False = built-in rules only, no AI calls for triage.</summary>
    public bool UseAi { get; set; } = true;
    /// <summary>Most "every 2 business days" reminders after one submission.</summary>
    public int MaxSubmissionFollowUps { get; set; } = 5;
    /// <summary>Where "Forward to the … team" tasks go (the draft is addressed here). Empty = you type the address.</summary>
    public Dictionary<string, string> TeamEmails { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record InboxJob(string Title, string? Client, string? Location, string Mode, string Type, decimal Rate,
    List<string> Skills, int? MinYears, List<string> Visas);
/// <param name="Id">Outlook's attachment id, used to download it.</param>
public sealed record InboxAttachment(string Id, string Name, long Size, string? ContentType);
/// <summary>A profile that fits a job email. The fields after ResumeReady were added later: matches stored before have them empty
/// until "Fetch matching profiles" recalculates them.</summary>
public sealed record InboxMatch(int ConsultantId, string Name, string Title, int Score, string Why, int Exp, bool ResumeReady,
    string? Visa = null, int SkillPct = 0, decimal Rate = 0, string? Location = null, List<string>? Matched = null, List<string>? Missing = null);

/// <summary>
/// Reads new mail from the user's Outlook Inbox folder and triages each email: category, priority, a one-line summary,
/// the job it describes (if any) matched against the user's own consultants, and the tasks / follow-ups / interviews it asks for.
/// Claude does the reading when configured; the built-in rules take over otherwise (and for obvious noise, to save AI calls).
/// </summary>
public sealed class InboxTriage(ClaudeClient claude, IOptions<InboxOptions> options, IOptions<MicrosoftOptions> ms,
    IHttpClientFactory http, ILogger<InboxTriage> log)
{
    public static readonly string[] Categories = ["JobListing", "Interview", "FollowUp", "Rate", "Rejection", "Other", "Noise"];
    public static readonly string[] Priorities = ["High", "Medium", "Low"];
    public static readonly string[] TaskKinds = ["Task", "FollowUp", "Interview"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static InboxJob? JobOf(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<InboxJob>(json, Json);
    public static List<InboxAttachment> AttachmentsOf(string? json) => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<InboxAttachment>>(json, Json) ?? [];
    public static List<InboxMatch> MatchesOf(string? json) => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<InboxMatch>>(json, Json) ?? [];

    /// <summary>Triages new Inbox mail for one user. Returns how many emails were added. Throws AppException like the mail sync.</summary>
    public async Task<int> ProcessAsync(AppDbContext db, int userId, string mailbox, string accessToken, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) return 0;
        var sent = await ProcessSentAsync(db, userId, mailbox, accessToken, ct);
        var since = await db.InboxItems.Where(i => i.UserId == userId && i.Direction == "In").MaxAsync(i => (DateTime?)i.ReceivedUtc, ct)
                    ?? DateTime.UtcNow.AddDays(-Math.Max(1, o.FirstSyncDays));
        var messages = await ReadFolderAsync("inbox", accessToken, since.AddMinutes(-2), o.MaxPerSync, ct);
        if (messages.Count == 0) return sent;

        var ids = messages.Select(m => Str(m["id"])).OfType<string>().ToList();
        var seen = (await db.InboxItems.Where(i => i.UserId == userId && ids.Contains(i.MessageId)).Select(i => i.MessageId).ToListAsync(ct)).ToHashSet();
        var bench = await InboxWorkflow.BenchForAsync(db, userId, ct);
        var added = 0;

        foreach (var m in messages)
        {
            var id = Str(m["id"]);
            if (id is null || id.Length > 400 || !seen.Add(id)) continue;
            var from = Str(m["from"]?["emailAddress"]?["address"])?.Trim().ToLowerInvariant() ?? "";
            if (from.Length == 0 || from.Equals(mailbox, StringComparison.OrdinalIgnoreCase)) continue;

            var item = new InboxItem
            {
                UserId = userId, MessageId = id, ConversationId = Str(m["conversationId"]) is { Length: <= 400 } c ? c : null,
                FromAddress = Cut(from, 256), FromName = Str(m["from"]?["emailAddress"]?["name"]) is { } n ? Cut(n, 200) : null,
                Subject = Cut(Str(m["subject"]) ?? "(no subject)", 400),
                Body = Cut(Clean(Str(m["body"]?["content"]) ?? Str(m["bodyPreview"]) ?? ""), 8000),
                ReceivedUtc = Date(m["receivedDateTime"]) ?? DateTime.UtcNow,
                HasAttachments = m["hasAttachments"]?.GetValue<bool>() == true,
                AttachmentsJson = AttachmentsJsonOf(m["attachments"]),
            };
            var important = Str(m["importance"]) == "high";
            var (tasks, interviewType) = await TriageAsync(item, important, bench, ct);
            // Workflow: who it's about, what it asks for, which team it's for, and the tasks that follow (InboxWorkflow).
            var text = item.Subject + "\n" + item.Body;
            item.ConsultantId ??= InboxWorkflow.FindConsultant(text, bench);
            if (item.Category != "Noise")
            {
                if (item.RequestsJson is null && InboxWorkflow.FindRequests(text) is { Count: > 0 } req) item.RequestsJson = InboxWorkflow.ToJson(req);
                item.Team ??= InboxWorkflow.FindTeam(text);
                if (item.Category == "Interview" && item.SlotsJson is null && InboxWorkflow.FindSlots(text, item.ReceivedUtc) is { Count: > 0 } sl) item.SlotsJson = InboxWorkflow.ToJson(sl);
            }
            var vendorReply = item.ConversationId != null && await db.InboxItems.AnyAsync(x => x.UserId == userId && x.Direction == "Out"
                && x.ConversationId == item.ConversationId && x.ReceivedUtc < item.ReceivedUtc, ct);
            var consultantName = bench.FirstOrDefault(b => b.Id == item.ConsultantId)?.Name;
            tasks.InsertRange(0, InboxWorkflow.IncomingTasks(item, vendorReply, consultantName, interviewType));
            db.InboxItems.Add(item);
            try
            {
                await db.SaveChangesAsync(ct);
                foreach (var t in tasks) { t.UserId = userId; t.InboxItemId = item.InboxItemId; db.InboxTasks.Add(t); }
                // They answered: the reminders on what we sent in this thread are done.
                if (item.ConversationId != null)
                    await db.InboxTasks.Where(t => t.UserId == userId && t.Status == "Open" && t.Action == "Nudge" && t.InboxItem != null
                            && t.InboxItem.ConversationId == item.ConversationId && t.InboxItem.Direction == "Out")
                        .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, "Done").SetProperty(t => t.ClosedUtc, DateTime.UtcNow)
                            .SetProperty(t => t.Notes, "They replied"), ct);
                await db.SaveChangesAsync(ct);
                added++;
            }
            catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e))
            {
                db.ChangeTracker.Clear();   // another sync stored it first
            }
        }
        if (added > 0) log.LogInformation("AI Inbox for user {User}: triaged {Count} new emails", userId, added);
        return added + sent;
    }

    /// <summary>Keeps a copy of what the user sent (Sent Items), so the AI Inbox can show it and tell which emails got no reply.
    /// Sent mail is stored as-is: no AI call.</summary>
    private async Task<int> ProcessSentAsync(AppDbContext db, int userId, string mailbox, string accessToken, CancellationToken ct)
    {
        var o = options.Value;
        var since = await db.InboxItems.Where(i => i.UserId == userId && i.Direction == "Out").MaxAsync(i => (DateTime?)i.ReceivedUtc, ct)
                    ?? DateTime.UtcNow.AddDays(-Math.Max(1, o.FirstSyncDays));
        var messages = await ReadFolderAsync("sentitems", accessToken, since.AddMinutes(-2), Math.Max(o.MaxPerSync, 100), ct);
        if (messages.Count == 0) return 0;
        var ids = messages.Select(m => Str(m["id"])).OfType<string>().ToList();
        var seen = (await db.InboxItems.Where(i => i.UserId == userId && ids.Contains(i.MessageId)).Select(i => i.MessageId).ToListAsync(ct)).ToHashSet();
        var own = new HashSet<string>(ms.Value.CompanyDomains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        if (mailbox.Contains('@')) own.Add(mailbox[(mailbox.IndexOf('@') + 1)..]);
        var bench = await InboxWorkflow.BenchForAsync(db, userId, ct);
        var added = 0;
        foreach (var m in messages)
        {
            var id = Str(m["id"]);
            if (id is null || id.Length > 400 || !seen.Add(id)) continue;
            var to = new[] { "toRecipients", "ccRecipients" }.SelectMany(k => (m[k] as JsonArray ?? []).Select(r => Str(r?["emailAddress"]?["address"])))
                .OfType<string>().Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList();
            var firstName = (m["toRecipients"] as JsonArray ?? []).Select(r => Str(r?["emailAddress"]?["name"])).FirstOrDefault(n => n != null && !n.Contains('@'));
            var body = Cut(Clean(Str(m["body"]?["content"]) ?? Str(m["bodyPreview"]) ?? ""), 8000);
            var item = new InboxItem
            {
                UserId = userId, MessageId = id, Direction = "Out", ConversationId = Str(m["conversationId"]) is { Length: <= 400 } c ? c : null,
                FromAddress = Cut(mailbox, 256), FromName = firstName is { } fn ? Cut(fn, 200) : null,   // for sent mail: the first recipient's name
                ToAddresses = Cut(string.Join(", ", to), 1000), Subject = Cut(Str(m["subject"]) ?? "(no subject)", 400), Body = body,
                ReceivedUtc = Date(m["sentDateTime"]) ?? Date(m["receivedDateTime"]) ?? DateTime.UtcNow,
                HasAttachments = m["hasAttachments"]?.GetValue<bool>() == true,
                AttachmentsJson = AttachmentsJsonOf(m["attachments"]),
                Category = "Sent", Priority = "Low", Status = "Done", Engine = "rules", Summary = Cut(FirstSentence(body), 600),
            };
            item.ConsultantId = InboxWorkflow.FindConsultant(item.Subject + "\n" + body, bench);
            var external = to.Any(a => a.Contains('@') && !own.Contains(a[(a.IndexOf('@') + 1)..]) && !IsNoise(a, ""));
            db.InboxItems.Add(item);
            try
            {
                await db.SaveChangesAsync(ct);
                if (item.ConversationId != null)
                {
                    // We answered / followed up: the reply tasks on their emails and our earlier reminders in this thread are done.
                    await db.InboxTasks.Where(t => t.UserId == userId && t.Status == "Open" && (t.Action == "Reply" || t.Action == "Nudge") && t.InboxItem != null
                            && t.InboxItem.ConversationId == item.ConversationId && t.InboxItemId != item.InboxItemId)
                        .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, "Done").SetProperty(t => t.ClosedUtc, DateTime.UtcNow), ct);
                }
                if (external)
                {
                    var firstInThread = item.ConversationId is null || !await db.InboxItems.AnyAsync(x => x.UserId == userId && x.Direction == "Out"
                        && x.ConversationId == item.ConversationId && x.InboxItemId != item.InboxItemId && x.ReceivedUtc < item.ReceivedUtc, ct);
                    var earlier = item.ConversationId is null ? 0 : await db.InboxTasks.CountAsync(t => t.UserId == userId && t.RepeatBusinessDays != null
                        && t.InboxItem != null && t.InboxItem.ConversationId == item.ConversationId, ct);
                    var name = bench.FirstOrDefault(b => b.Id == item.ConsultantId)?.Name;
                    foreach (var t in InboxWorkflow.SentTasks(item, firstInThread, earlier, name, o.MaxSubmissionFollowUps))
                    { t.UserId = userId; t.InboxItemId = item.InboxItemId; db.InboxTasks.Add(t); }
                    await db.SaveChangesAsync(ct);
                }
                added++;
            }
            catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e)) { db.ChangeTracker.Clear(); }
        }
        return added;
    }

    // =====================================================================================================
    // Triage
    // =====================================================================================================

    private async Task<(List<InboxTask> Tasks, string? InterviewType)> TriageAsync(InboxItem item, bool important, List<ConsultantDto> bench, CancellationToken ct)
    {
        string? interviewType = null;
        var text = item.Subject + "\n" + item.Body;
        var tasks = new List<InboxTask>();
        InboxJob? job = null;

        var noise = IsNoise(item.FromAddress, text);
        JsonObject? ai = null;
        if (!noise && options.Value.UseAi && claude.Enabled)
            ai = await claude.JsonAsync("inbox-triage", SystemPrompt, UserPrompt(item), 900, ct);

        if (ai != null)
        {
            item.Engine = "claude";
            item.Category = Pick(S(ai["category"]), Categories) ?? "Other";
            item.Priority = Pick(S(ai["priority"]), Priorities) ?? "Medium";
            item.PriorityReason = Cut(S(ai["priorityReason"]) ?? "", 300);
            item.Summary = Cut(S(ai["summary"]) ?? FirstSentence(item.Body), 600);
            item.NeedsReply = ai["needsReply"] is JsonValue nr && nr.TryGetValue<bool>(out var b) && b;
            if (item.Category == "JobListing" && ai["job"] is JsonObject j) job = JobFromAi(j, text);
            if ((ai["requests"] as JsonArray)?.Select(S).OfType<string>().Select(x => Cut(x, 40)).Distinct().Take(8).ToList() is { Count: > 0 } rq) item.RequestsJson = InboxWorkflow.ToJson(rq);
            item.Team = Pick(S(ai["team"]), InboxWorkflow.Teams);
            interviewType = Pick(S(ai["interviewType"]), InboxWorkflow.InterviewTypes);
            var slots = ((ai["slots"] as JsonArray) ?? []).Select(S).OfType<string>()
                .Select(x => DateTimeOffset.TryParse(x, out var d) ? d.UtcDateTime : (DateTime?)null).OfType<DateTime>().Where(d => d > DateTime.UtcNow.AddHours(-12)).Take(3).ToList();
            if (slots.Count > 0) item.SlotsJson = InboxWorkflow.ToJson(slots);
            // Interviews, follow-ups, details requests and forwards come from the workflow rules; the AI adds other concrete tasks.
            foreach (var a in (ai["actions"] as JsonArray ?? []).OfType<JsonObject>().Where(a => Pick(S(a["kind"]), TaskKinds) is "Task").Take(2))
            {
                var kind = "Task";
                var title = S(a["title"]);
                if (string.IsNullOrWhiteSpace(title)) continue;
                tasks.Add(new InboxTask
                {
                    Kind = kind, Title = Cut(title, 300), Auto = true,
                    DueUtc = S(a["due"]) is { } d && DateTimeOffset.TryParse(d, out var due) ? due.UtcDateTime : null,
                    DurationMinutes = a["durationMinutes"] is JsonValue dm && dm.TryGetValue<int>(out var mins) && mins is > 0 and <= 480 ? mins : null,
                    Location = S(a["location"]) is { } loc ? Cut(loc, 500) : null,
                });
            }
        }
        else
        {
            RulesTriage(item, noise, important);
            if (item.Category == "JobListing") job = JobFromRules(text);
        }

        // Match the job against the user's own consultants (FRD-007) and flag the strong ones for the resume tailor (FRD-008).
        if (job != null && job.Skills.Count > 0)
        {
            item.JobJson = JsonSerializer.Serialize(job, Json);
            var o = options.Value;
            var matches = InboxWorkflow.Match(job, bench, o.MatchMinScore, o.ResumeReadyScore);
            if (matches.Count > 0)
            {
                item.MatchesJson = JsonSerializer.Serialize(matches, Json);
                item.NeedsReply = true;
                var best = matches[0];
                if (best.ResumeReady)
                {
                    item.Priority = "High";
                    item.PriorityReason = $"{best.Name} fits this job ({best.Score}%, {best.Exp} yrs)";
                    if (!tasks.Any(t => t.Kind == "Task" && t.Title.StartsWith("Submit", StringComparison.Ordinal)))
                        tasks.Add(new InboxTask { Kind = "Task", Auto = true, Title = Cut($"Submit {best.Name} for {job.Title}", 300), DueUtc = DateTime.UtcNow.AddHours(4), ConsultantId = best.ConsultantId });
                }
            }
        }
        if (important && item.Category != "Noise" && item.Priority == "Low") { item.Priority = "Medium"; item.PriorityReason ??= "Marked important by the sender"; }
        if (string.IsNullOrWhiteSpace(item.Summary)) item.Summary = Cut(FirstSentence(item.Body), 600);
        if (string.IsNullOrWhiteSpace(item.PriorityReason)) item.PriorityReason = null;
        return (tasks, interviewType);
    }

    /// <summary>"Fetch matching profiles": re-matches recent job emails against today's bench (skills plus what each latest
    /// resume shows). Returns how many job emails were checked and how many now have matches.</summary>
    public async Task<(int Jobs, int WithMatches, int Consultants)> RematchAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var o = options.Value;
        var bench = await InboxWorkflow.BenchForAsync(db, userId, ct);
        var since = DateTime.UtcNow.AddDays(-14);
        var items = await db.InboxItems.Where(i => i.UserId == userId && i.Direction == "In" && i.Category == "JobListing" && i.ReceivedUtc >= since).ToListAsync(ct);
        var with = 0;
        foreach (var i in items)
        {
            var job = JobOf(i.JobJson);
            if (job is null || job.Skills.Count == 0) { job = JobFromRules(i.Subject + "\n" + i.Body); if (job.Skills.Count == 0) continue; i.JobJson = JsonSerializer.Serialize(job, Json); }
            var matches = InboxWorkflow.Match(job, bench, o.MatchMinScore, o.ResumeReadyScore);
            i.MatchesJson = matches.Count > 0 ? JsonSerializer.Serialize(matches, Json) : null;
            if (matches.Count > 0) { with++; i.NeedsReply = true; if (matches[0].ResumeReady && i.Status == "New") { i.Priority = "High"; i.PriorityReason = $"{matches[0].Name} fits this job ({matches[0].Score}%, {matches[0].Exp} yrs)"; } }
        }
        await db.SaveChangesAsync(ct);
        return (items.Count, with, bench.Count);
    }

    private const string SystemPrompt = """
        You triage the Outlook inbox of a US IT bench-sales recruiter. Bench sales means marketing the recruiter's own
        consultants (H1B, GC, USC, OPT…) to vendors and implementation partners for C2C/W2 contract roles.
        Read one email and return JSON with:
          category: JobListing (a vendor/recruiter shares a requirement or job description), Interview (schedule, reschedule,
            confirm or feedback on an interview), FollowUp (asks for something or chases a reply: resume, RTR, details, status),
            Rate (rate negotiation or confirmation), Rejection (not moving forward, filled, on hold, closed), Other, Noise
            (newsletters, marketing, automated notifications).
          priority: High (an interview, an urgent or new requirement, anything with a deadline today), Medium (needs a
            reply in a day or two), Low (FYI or noise).
          priorityReason: under 15 words.
          summary: one or two sentences, what the sender wants.
          needsReply: true if the recruiter should answer.
          job: for JobListing only, else null: {title, client (or null), location (or null), mode: Remote|Hybrid|Onsite,
            type: C2C|W2|1099, rate: number per hour (0 if not given), skills: up to 12 canonical technical skills,
            minYears: number or null, visas: accepted visas (empty if not stated)}.
          actions: up to 3 concrete next steps: {kind: Task|FollowUp|Interview, title (imperative, under 12 words),
            due: ISO-8601 date-time with offset if the email gives a time or deadline else null, durationMinutes (interviews),
            location: meeting link, phone or address for interviews else null}. Use Interview only for an actual interview slot.
          requests: what the sender asks us to send, as short items (e.g. "Updated resume", "RTR", "Visa copy", "DL / State ID",
            "I-94", "Passport copy", "References", "Skill matrix", "Interview availability"), else [].
          team: "Contracts" (MSA, SOW, PO, W-9, payment terms), "Insurance" (COI, liability insurance) or "Immigration" (LCA,
            petition, H1B transfer, I-9 / E-Verify) when the email is a question for that team, else null.
          slots: up to 3 interview time slots offered, ISO-8601 with the offset of the time zone written (US Eastern if none), else [].
          interviewType: for interviews, "Client call", "Technical call", "L1", "L2", "Final" or null.
        Never invent facts that are not in the email.
        """;

    private static string UserPrompt(InboxItem i) =>
        $"Now (UTC): {DateTime.UtcNow:yyyy-MM-ddTHH:mm}Z\nFrom: {i.FromName} <{i.FromAddress}>\nSubject: {i.Subject}\n\n{Cut(i.Body, 4000)}";

    private static InboxJob JobFromAi(JsonObject j, string text)
    {
        var skills = ((j["skills"] as JsonArray) ?? []).Select(S).OfType<string>()
            .Select(s => Rules.Skills.FirstOrDefault(k => k.Equals(s, StringComparison.OrdinalIgnoreCase)) ?? s.Trim())
            .Concat(Rules.FindSkills(text)).Distinct(StringComparer.OrdinalIgnoreCase).Take(14).ToList();
        var visas = ((j["visas"] as JsonArray) ?? []).Select(S).OfType<string>().Select(v => v.Trim()).Where(v => v.Length is > 0 and <= 20).Distinct().ToList();
        return new InboxJob(
            Cut(S(j["title"]) ?? "Requirement", 200), S(j["client"]) is { } c ? Cut(c, 200) : null, S(j["location"]) is { } l ? Cut(l, 200) : null,
            Pick(S(j["mode"]), Domain.WorkModes) ?? "Onsite", Pick(S(j["type"]), Domain.ContractTypes) ?? "C2C",
            j["rate"] is JsonValue r && r.TryGetValue<decimal>(out var rate) && rate is >= 0 and <= 500 ? rate : 0,
            skills, j["minYears"] is JsonValue y && y.TryGetValue<int>(out var yrs) && yrs is > 0 and < 40 ? yrs : null, visas);
    }

    private static InboxJob JobFromRules(string text)
    {
        var r = Rules.ExtractRequirement(text);
        var years = Regex.Match(text, @"(\d{1,2})\s*\+?\s*(?:years|yrs)", RegexOptions.IgnoreCase);
        var title = string.IsNullOrWhiteSpace(r.Title) ? Regex.Replace(text.Split('\n')[0], @"^(re|fw|fwd)\s*:\s*", "", RegexOptions.IgnoreCase) : r.Title!;
        return new InboxJob(Cut(title.Trim(), 200), string.IsNullOrWhiteSpace(r.Client) ? null : r.Client, string.IsNullOrWhiteSpace(r.Location) ? null : r.Location,
            r.Mode ?? "Onsite", r.Type ?? "C2C", r.Rate, r.Skills ?? [], years.Success && int.TryParse(years.Groups[1].Value, out var y) && y < 40 ? y : null,
            (r.Visas ?? []).Where(v => v != "Any").ToList());
    }

    // ---------- built-in rules ----------
    private static readonly Regex NoiseFrom = new(@"(no-?reply|donotreply|notifications?|newsletter|mailer-daemon|marketing|updates?)@|@(linkedin|facebookmail|google|microsoft|indeed|dice|zoom)\.", RegexOptions.IgnoreCase);
    private static readonly Regex JobRe = new(@"\b(requirements?|job description|JD|position|opening|opportunity|job alert|we are looking for|looking for an?|need an?|hiring|role\s*:|title\s*:)", RegexOptions.IgnoreCase);
    private static readonly Regex JobTerms = new(@"\b(C2C|W2|1099|contract|duration|months?|visa|H1B|H-1B|GC|USC|EAD|onsite|hybrid|remote|rate)\b", RegexOptions.IgnoreCase);
    // An actual interview: the word itself, a screening call, or asking for slots/availability for a call. A bare Teams/Zoom link is not enough.
    private static readonly Regex InterviewRe = new(@"\binterview|screening call|time ?slots?|(schedule|availability|available)\b.{0,40}\b(call|interview|discussion)\b", RegexOptions.IgnoreCase);
    // Calendar traffic (stand-ups, syncs, cancellations): not an interview unless it says so.
    private static readonly Regex MeetingRe = new(@"^(canceled|cancelled|accepted|declined|tentative|updated invitation|invitation)\s*:|microsoft teams meeting|join the meeting now|join zoom meeting|\b(stand ?up|sync ?up|standup|scrum call)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex SystemRe = new(@"^(undeliverable|delivery (status notification|has failed)|automatic reply|out of office)\b|verification code|one-time (pass)?code|security code|\bOTP\b|password reset|confirm your (email|account)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    // A copy of a submission we sent ourselves (CC/BCC or forwarded by a colleague).
    private static readonly Regex OwnSubmissionRe = new(@"^(fw|fwd)?:?\s*submission\s*:", RegexOptions.IgnoreCase);
    private static readonly Regex RejectRe = new(@"not moving forward|not selected|rejected|position (has been |is )?(filled|closed|on hold)|went with another", RegexOptions.IgnoreCase);
    private static readonly Regex RateRe = new(@"rate confirmation|confirm (the |your )?rate|best rate|final rate|rate negotiation", RegexOptions.IgnoreCase);
    private static readonly Regex AskRe = new(@"follow(ing)? up|any update|checking in|please (send|share|confirm|provide)|kindly (send|share)|need (the|your)|\bRTR\b|right to represent|\?", RegexOptions.IgnoreCase);
    private static readonly Regex UrgentRe = new(@"\b(urgent|immediate(ly)?|asap|hot (req|requirement)|priority|today|EOD|end of day)\b", RegexOptions.IgnoreCase);

    /// <summary>Bulk, automated and system mail (bounces, codes): skipped by the AI and kept out of the main inbox view.</summary>
    private static bool IsNoise(string from, string text) =>
        SystemRe.IsMatch(text[..Math.Min(text.Length, 400)]) || from.StartsWith("microsoftexchange", StringComparison.OrdinalIgnoreCase) || from.StartsWith("postmaster@", StringComparison.OrdinalIgnoreCase)
        || ((NoiseFrom.IsMatch(from) || Regex.IsMatch(text, @"\bunsubscribe\b", RegexOptions.IgnoreCase)) && !InterviewRe.IsMatch(text[..Math.Min(text.Length, 300)]));

    private static void RulesTriage(InboxItem i, bool noise, bool important)
    {
        var text = i.Subject + "\n" + i.Body;
        var head = i.Subject + "\n" + Cut(i.Body, 1500);
        var skills = Rules.FindSkills(head).Count;
        var jobSubject = JobRe.IsMatch(i.Subject) && !Regex.IsMatch(i.Subject, @"\binterview\b", RegexOptions.IgnoreCase);
        var meeting = MeetingRe.IsMatch(head) && !Regex.IsMatch(head, @"\b(interview|screening)\b", RegexOptions.IgnoreCase);
        (i.Category, i.Priority, i.PriorityReason) =
            noise ? ("Noise", "Low", "Automated or bulk email")
            : OwnSubmissionRe.IsMatch(i.Subject) ? ("Other", "Low", "Copy of a submission sent by your team")
            : meeting ? ("Other", "Low", "Calendar invite")
            : RejectRe.IsMatch(head) ? ("Rejection", "Medium", "Vendor update on a submission")
            : jobSubject && (JobTerms.IsMatch(text) || skills >= 2) ? JobPriority(head)
            : InterviewRe.IsMatch(head) ? ("Interview", "High", "Interview to schedule or confirm")
            : RateRe.IsMatch(head) ? ("Rate", "Medium", "Rate to confirm")
            : JobRe.IsMatch(head) && (JobTerms.IsMatch(text) || skills >= 2) ? JobPriority(head)
            : AskRe.IsMatch(head) ? ("FollowUp", "Medium", "Asks for a reply")
            : ("Other", important ? "Medium" : "Low", important ? "Marked important by the sender" : null);
        i.NeedsReply = i.Category is "Interview" or "FollowUp" or "Rate";
        i.Summary = Cut(FirstSentence(i.Body), 600);
        i.Engine = "rules";
    }

    private static (string, string, string?) JobPriority(string head) =>
        UrgentRe.IsMatch(head) ? ("JobListing", "High", "Urgent requirement") : ("JobListing", "Medium", "New requirement");

    private static IEnumerable<InboxTask> RuleTasks(InboxItem i)
    {
        var who = i.FromName ?? i.FromAddress;
        return [];   // tasks come from InboxWorkflow.IncomingTasks
    }

    // ---------- Graph ----------
    /// <param name="folder">Well-known folder name: inbox or sentitems.</param>
    private async Task<List<JsonNode>> ReadFolderAsync(string folder, string accessToken, DateTime since, int max, CancellationToken ct)
    {
        var url = $"{ms.Value.GraphUrl.TrimEnd('/')}/v1.0/me/mailFolders/{folder}/messages"
                  + "?$select=id,conversationId,subject,from,toRecipients,ccRecipients,receivedDateTime,sentDateTime,body,bodyPreview,hasAttachments,importance"
                  + "&$expand=attachments($select=id,name,size,contentType,isInline)"
                  + $"&$filter=receivedDateTime ge {since:yyyy-MM-ddTHH:mm:ssZ}&$orderby=receivedDateTime asc&$top=25";
        var list = new List<JsonNode>();
        var client = http.CreateClient("microsoft");
        while (url != null && list.Count < max)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new("Bearer", accessToken);
            req.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
            using var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AppException(401, "MAIL_RECONNECT", "Outlook needs to be reconnected", "Outlook refused access to the inbox. Connect Outlook again.");
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Graph inbox {Status}: {Body}", (int)res.StatusCode, body.Length > 300 ? body[..300] : body);
                throw new AppException(502, "MAIL_ERROR", "Couldn't read Outlook", "Outlook returned an error. The next sync will try again.");
            }
            var j = JsonNode.Parse(body);
            list.AddRange(((j?["value"] as JsonArray) ?? []).OfType<JsonNode>());
            url = Str(j?["@odata.nextLink"]);
        }
        return list.Take(max).ToList();
    }

    // ---------- attachments ----------
    /// <summary>Real files only: inline images (signature logos) are left out.</summary>
    private static string AttachmentsJsonOf(JsonNode? list) => JsonSerializer.Serialize(
        ((list as JsonArray) ?? []).OfType<JsonObject>()
            .Where(a => !(a["isInline"] is JsonValue v && v.TryGetValue<bool>(out var inl) && inl))
            .Select(a => (Id: Str(a["id"]), Name: Str(a["name"]), Size: a["size"] is JsonValue sv && sv.TryGetValue<long>(out var sz) ? sz : 0, Type: Str(a["contentType"])))
            .Where(a => a.Id != null && a.Name != null)
            .Take(20).Select(a => new InboxAttachment(a.Id!, Cut(a.Name!, 200), a.Size, a.Type)).ToList(), Json);

    /// <summary>The email's attachment list, read from Outlook (for emails stored before attachments were recorded).</summary>
    public async Task<string> ReadAttachmentsAsync(string accessToken, string messageId, CancellationToken ct)
    {
        var url = $"{ms.Value.GraphUrl.TrimEnd('/')}/v1.0/me/messages/{Uri.EscapeDataString(messageId)}/attachments?$select=id,name,size,contentType,isInline";
        var j = await GraphGetAsync(accessToken, url, ct);
        return AttachmentsJsonOf(j?["value"]);
    }

    /// <summary>Downloads one attachment from Outlook. Only file attachments carry bytes (not attached emails or calendar items).</summary>
    public async Task<(byte[] Bytes, string Name, string ContentType)> DownloadAttachmentAsync(string accessToken, string messageId, string attachmentId, CancellationToken ct)
    {
        var url = $"{ms.Value.GraphUrl.TrimEnd('/')}/v1.0/me/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}";
        var j = await GraphGetAsync(accessToken, url, ct);
        var b64 = Str(j?["contentBytes"]) ?? throw AppException.Bad("NOT_A_FILE", "Can't download this attachment", "It's an attached email or calendar item. Open it in Outlook.");
        return (Convert.FromBase64String(b64), Str(j?["name"]) ?? "attachment", Str(j?["contentType"]) ?? "application/octet-stream");
    }

    private async Task<JsonNode?> GraphGetAsync(string accessToken, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", accessToken);
        using var res = await http.CreateClient("microsoft").SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.NotFound) throw AppException.NotFound("Attachment");
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AppException(401, "MAIL_RECONNECT", "Outlook needs to be reconnected", "Outlook refused access. Connect Outlook again.");
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Graph attachments {Status}: {Body}", (int)res.StatusCode, body.Length > 300 ? body[..300] : body);
            throw new AppException(502, "MAIL_ERROR", "Couldn't read Outlook", "Outlook returned an error. Try again in a minute.");
        }
        return JsonNode.Parse(body);
    }

    // ---------- helpers ----------
    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static string? Str(JsonNode? n) => S(n);
    private static string? Pick(string? value, IEnumerable<string> allowed) => value is null ? null : allowed.FirstOrDefault(a => a.Equals(value, StringComparison.OrdinalIgnoreCase));
    private static DateTime? Date(JsonNode? n) => Str(n) is { } s && DateTimeOffset.TryParse(s, out var d) ? d.UtcDateTime : null;
    public static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
    /// <summary>Drops quoted replies and collapses blank runs so the AI and the reader see the new part of the email.</summary>
    private static string Clean(string body)
    {
        // Image placeholders Outlook leaves in the text version ("[cid:image001.png@01DC…]") mean nothing to a reader.
        var t = Regex.Replace(body.Replace("\r\n", "\n"), @"\[cid:[^\]]*\]", "", RegexOptions.IgnoreCase);
        var cut = Regex.Match(t, @"^\s*(From:\s.+\n\s*Sent:|On .{6,80} wrote:|-{2,}\s*Original Message\s*-{2,})", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (cut.Success && cut.Index > 40) t = t[..cut.Index];
        return Regex.Replace(Regex.Replace(t, @"[ \t]+", " "), @"\n{3,}", "\n\n").Trim();
    }
    private static string FirstSentence(string body)
    {
        var t = Regex.Replace(body, @"^(hi|hello|dear|good (morning|afternoon))[^\n]{0,40}\n", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        t = Regex.Replace(t, @"\s+", " ").Trim();
        var m = Regex.Match(t, @"^.{20,240}?[.!?](\s|$)");
        return m.Success ? m.Value.Trim() : Cut(t, 240);
    }
}
