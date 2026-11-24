using System.Text;
using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Controllers;

/// <summary>
/// AI Inbox: the signed-in user's own Outlook Inbox, triaged by AI. High-priority job emails, follow-up drafts,
/// tasks and interviews taken from emails, and job emails matched to the user's consultants. Always the caller's own mailbox.
/// </summary>
[ApiController, Route("api/inbox")]
public sealed class InboxController(AppDbContext db, ICurrentUser me, MailSync sync, MailSender sender, ClaudeClient claude,
    IOptions<MicrosoftOptions> ms, AuditService audit, MicrosoftAuth auth, InboxTriage triage, IOptions<InboxOptions> triageOptions) : ControllerBase
{
    private static readonly string[] Statuses = ["New", "Done", "Dismissed"];
    private static readonly string[] TaskStatuses = ["Open", "Done", "Dismissed"];

    /// <summary>Emails, newest first. <paramref name="view"/>: all (default, no noise) | high | jobs | reply | interviews | noise | done |
    /// sent (what you sent, with whether it got a reply) | awaiting (sent, no reply yet).</summary>
    [HttpGet, RequirePerm("mail.view")]
    public async Task<List<InboxItemDto>> List([FromQuery] string? view, [FromQuery] string? q, CancellationToken ct)
    {
        var meId = me.RequiredId;
        var all = db.InboxItems.AsNoTracking().Where(i => i.UserId == meId);
        var sentView = view is "sent" or "awaiting";
        var items = all.Where(i => i.Direction == (sentView ? "Out" : "In"));
        items = (view ?? "all") switch
        {
            "sent" => items,
            "awaiting" => items.Where(i => i.ConversationId == null || !all.Any(r => r.Direction == "In" && r.ConversationId == i.ConversationId && r.ReceivedUtc > i.ReceivedUtc)),
            "done" => items.Where(i => i.Status != "New"),
            "noise" => items.Where(i => i.Status == "New" && i.Category == "Noise"),
            "high" => items.Where(i => i.Status == "New" && i.Priority == "High"),
            "jobs" => items.Where(i => i.Status == "New" && i.Category == "JobListing"),
            "reply" => items.Where(i => i.Status == "New" && i.NeedsReply),
            "interviews" => items.Where(i => i.Status == "New" && i.Category == "Interview"),
            "matches" => items.Where(i => i.Category == "JobListing" && i.MatchesJson != null && i.Status != "Dismissed"),
            _ => items.Where(i => i.Status == "New" && i.Category != "Noise"),
        };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            items = items.Where(i => i.Subject.Contains(term) || i.FromAddress.Contains(term) || (i.FromName != null && i.FromName.Contains(term))
                                     || (i.ToAddresses != null && i.ToAddresses.Contains(term)) || i.Summary.Contains(term));
        }
        var rows = await items.OrderByDescending(i => i.ReceivedUtc).Take(300)
            .Select(i => new
            {
                i, Open = db.InboxTasks.Count(t => t.InboxItemId == i.InboxItemId && t.Status == "Open"),
                // The other side's latest message in the same thread after this one: a reply to what we sent, or our answer to what we got.
                Answered = all.Where(r => r.ConversationId != null && r.ConversationId == i.ConversationId && r.Direction != i.Direction && r.ReceivedUtc > i.ReceivedUtc)
                    .Max(r => (DateTime?)r.ReceivedUtc),
            }).ToListAsync(ct);
        // Inbox: high first, then newest, so what needs attention is on top. Sent: newest first.
        return rows.Select(r => ToDto(r.i, r.Open, r.Answered))
            .OrderBy(d => !sentView && d.Status == "New" ? Array.IndexOf(InboxTriage.Priorities, d.Priority) : 9).ThenByDescending(d => d.ReceivedUtc).ToList();
    }

    /// <summary>Counts for the inbox tile and the browser alert (polled every minute).</summary>
    [HttpGet("summary"), RequirePerm("mail.view")]
    public async Task<InboxSummaryDto> Summary(CancellationToken ct)
    {
        var meId = me.RequiredId;
        var conn = await db.MailConnections.AsNoTracking().Where(c => c.UserId == meId).Select(c => new { c.Status, c.LastSyncUtc }).FirstOrDefaultAsync(ct);
        var mine = db.InboxItems.AsNoTracking().Where(i => i.UserId == meId);
        var open = mine.Where(i => i.Direction == "In" && i.Status == "New" && i.Category != "Noise");
        // Sent in the last 14 days, no answer in the thread yet, and at least a day old: worth a nudge.
        var nudgeFrom = DateTime.UtcNow.AddDays(-14); var nudgeTo = DateTime.UtcNow.AddDays(-1);
        var awaiting = await mine.CountAsync(i => i.Direction == "Out" && i.ReceivedUtc >= nudgeFrom && i.ReceivedUtc <= nudgeTo
            && (i.ConversationId == null || !mine.Any(r => r.Direction == "In" && r.ConversationId == i.ConversationId && r.ReceivedUtc > i.ReceivedUtc)), ct);
        var counts = await open.GroupBy(_ => 1).Select(g => new
        {
            Open = g.Count(), High = g.Count(i => i.Priority == "High"), Jobs = g.Count(i => i.Category == "JobListing"), Reply = g.Count(i => i.NeedsReply),
        }).FirstOrDefaultAsync(ct);
        var latest = await open.Where(i => i.Priority == "High").OrderByDescending(i => i.ReceivedUtc)
            .Select(i => new { i.InboxItemId, i.Subject, From = i.FromName ?? i.FromAddress, i.ReceivedUtc }).FirstOrDefaultAsync(ct);
        var endOfDay = DateTime.UtcNow.AddHours(24);
        var tasks = db.InboxTasks.AsNoTracking().Where(t => t.UserId == meId && t.Status == "Open");
        var interviews = await tasks.CountAsync(t => t.Kind == "Interview" && (t.DueUtc == null || t.DueUtc >= DateTime.UtcNow.AddHours(-2)), ct);
        var due = await tasks.CountAsync(t => t.DueUtc != null && t.DueUtc <= endOfDay, ct);
        return new InboxSummaryDto(ms.Value.Configured, conn?.Status == "Active", conn?.LastSyncUtc, counts?.Open ?? 0, counts?.High ?? 0, counts?.Jobs ?? 0,
            counts?.Reply ?? 0, interviews, due, latest?.InboxItemId, latest?.Subject, latest?.From, latest?.ReceivedUtc, awaiting);
    }

    /// <summary>Reads new Outlook mail now (vendor tracking + AI Inbox) instead of waiting for the next scheduled sync.</summary>
    [HttpPost("sync"), RequirePerm("mail.view"), EnableRateLimiting("ai")]
    public async Task<InboxSyncResult> Sync(CancellationToken ct)
    {
        var meId = me.RequiredId;
        var before = await db.InboxItems.CountAsync(i => i.UserId == meId, ct);
        var r = await sync.SyncAsync(meId, ct);
        if (r.Status == "NotConnected")
            throw AppException.Conflict("MAIL_NOT_CONNECTED", "Outlook isn't connected", "Sign in with Microsoft (or connect Outlook on Vendor Emails) to see your AI Inbox.");
        var after = await db.InboxItems.CountAsync(i => i.UserId == meId, ct);
        return new InboxSyncResult(r.Read, r.Status, r.Error, after - before);
    }

    [HttpGet("{id:int}"), RequirePerm("mail.view")]
    public async Task<InboxDetailDto> Get(int id, CancellationToken ct)
    {
        var item = await Own(id, ct);
        // Stored before attachments were recorded: read the list from Outlook once and keep it. Best effort.
        if (item.HasAttachments && item.AttachmentsJson is null)
        {
            try
            {
                var json = await triage.ReadAttachmentsAsync(await TokenAsync(ct), item.MessageId, ct);
                await db.InboxItems.Where(i => i.InboxItemId == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.AttachmentsJson, json), ct);
                item.AttachmentsJson = json;
            }
            catch (AppException) { /* shown as "attachments" without names; Outlook may be disconnected */ }
        }
        var tasks = await TaskQuery(db.InboxTasks.AsNoTracking().Where(t => t.InboxItemId == id && t.UserId == item.UserId)).ToListAsync(ct);
        return new InboxDetailDto(ToDto(item, tasks.Count(t => t.Status == "Open"), await AnsweredAsync(item, ct)), item.Body, tasks);
    }

    /// <summary>Marks an email done / dismissed / new again, or links the requirement saved from it.</summary>
    [HttpPatch("{id:int}"), RequirePerm("mail.view")]
    public async Task<InboxItemDto> Update(int id, InboxUpdateInput input, CancellationToken ct)
    {
        var item = await db.InboxItems.FirstOrDefaultAsync(i => i.InboxItemId == id && i.UserId == me.RequiredId, ct) ?? throw AppException.NotFound("Email");
        if (input.Status is { } s)
        {
            if (!Statuses.Contains(s)) throw AppException.Bad("VALIDATION", "Invalid status", "Status must be New, Done or Dismissed.");
            item.Status = s;
        }
        if (input.RequirementId is int rid)
        {
            if (!await db.Requirements.AnyAsync(r => r.RequirementId == rid, ct)) throw AppException.NotFound("Requirement");
            item.RequirementId = rid;
        }
        await db.SaveChangesAsync(ct);
        return ToDto(item, await db.InboxTasks.CountAsync(t => t.InboxItemId == id && t.Status == "Open", ct));
    }

    /// <summary>AI-written reply to the email (FRD-004), ready to review in the email window. Nothing is sent.
    /// For a job email it pitches the chosen consultant (or the best match).</summary>
    [HttpPost("{id:int}/draft"), RequirePerm("mail.view"), EnableRateLimiting("ai")]
    public async Task<InboxDraftDto> Draft(int id, InboxDraftInput input, CancellationToken ct)
    {
        var item = await Own(id, ct);
        if (item.Direction == "Out") return await NudgeAsync(item, input, ct);
        var job = InboxTriage.JobOf(item.JobJson);
        var matches = InboxTriage.MatchesOf(item.MatchesJson);
        var pickId = input.ConsultantId ?? matches.FirstOrDefault()?.ConsultantId;
        var c = pickId is int cid
            ? await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(x => x.ConsultantId == cid)
                .Select(x => new { x.FullName, x.Title, x.ExperienceYears, x.VisaStatus, x.Location, x.OpenToRelocate, x.Skills, x.Availability }).FirstOrDefaultAsync(ct)
            : null;
        var first = (item.FromName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { Length: > 1 } f && !f.Contains('@') ? f : "there";

        string? body = null;
        if (claude.Enabled)
        {
            var ctx = new StringBuilder();
            ctx.AppendLine($"Email from {item.FromName} <{item.FromAddress}>, subject \"{item.Subject}\":\n{InboxTriage.Cut(item.Body, 3500)}\n");
            if (job != null) ctx.AppendLine($"Job: {job.Title}{(job.Client is { } cl ? " at " + cl : "")}, {job.Location} {job.Mode}, {job.Type}, skills {string.Join(", ", job.Skills)}.");
            if (c != null) ctx.AppendLine($"Consultant to propose: {c.FullName}, {c.Title}, {c.ExperienceYears} years, visa {c.VisaStatus}, in {c.Location}{(c.OpenToRelocate ? " (open to relocate)" : "")}, " +
                                          $"skills {string.Join(", ", c.Skills.Take(10))}{(c.Availability is { } av ? ", available " + av : "")}. Do not mention their pay rate.");
            if (!string.IsNullOrWhiteSpace(input.Instructions)) ctx.AppendLine("Recruiter's instructions: " + InboxTriage.Cut(input.Instructions.Trim(), 500));
            body = await claude.TextAsync("inbox-draft",
                "You write email replies for a US IT bench-sales recruiter to vendor recruiters. Plain text, friendly and professional, " +
                "under 140 words. Start with a greeting using the sender's first name. Answer what the email asks; for a job, propose the " +
                "consultant in two or three lines and offer to send the resume with the RTR. Never invent facts. No subject line and no " +
                "signature or sign-off name (the app adds the recruiter's signature).", ctx.ToString(), 600, fast: false, ct: ct);
        }
        var engine = body is null ? "rules" : "claude";
        body ??= item.Category switch
        {
            "JobListing" when c != null =>
                $"Hi {first},\n\nThanks for sharing the {job?.Title ?? "requirement"}. I have {c.FullName}, a {c.Title} with {c.ExperienceYears}+ years " +
                $"({string.Join(", ", c.Skills.Take(5))}), on {c.VisaStatus} in {c.Location}{(c.OpenToRelocate ? " and open to relocate" : "")}. " +
                "I think they're a strong fit.\n\nCould you share the bill rate and the client? I'll send the resume with the RTR right away.",
            "JobListing" =>
                $"Hi {first},\n\nThanks for sharing the {job?.Title ?? "requirement"}. I'm checking my bench now. Could you confirm the bill rate, the client and the visas accepted? " +
                "I'll come back with a matching profile shortly.",
            "Interview" =>
                $"Hi {first},\n\nThanks for the interview request. Could you share two or three time slots (with time zone) and the interview format? " +
                "I'll confirm with the consultant and get back to you right away.",
            "Rate" => $"Hi {first},\n\nThanks for your email. I'm confirming the rate with the consultant and will get back to you today.",
            "Rejection" => $"Hi {first},\n\nThanks for letting me know. I have other strong profiles on my bench; please keep me posted on similar requirements.",
            _ => $"Hi {first},\n\nThanks for your email. I'm looking into this and will get back to you by end of day.",
        };
        var subject = item.Subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) ? item.Subject : "RE: " + item.Subject;
        return new InboxDraftDto(item.FromAddress, InboxTriage.Cut(subject, 255), body.Trim(), item.InboxItemId, engine, engine == "rules" ? claude.Notice : null);
    }

    /// <summary>Follow-up on something you sent that got no answer: a short nudge in the same thread, to the same people.</summary>
    private async Task<InboxDraftDto> NudgeAsync(InboxItem item, InboxDraftInput input, CancellationToken ct)
    {
        var to = (item.ToAddresses ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(a => !a.Equals(item.FromAddress, StringComparison.OrdinalIgnoreCase)).ToList();
        if (to.Count == 0) throw AppException.Bad("NO_RECIPIENT", "Nobody to follow up with", "This email has no recipients other than you.");
        var first = (item.FromName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { Length: > 1 } f ? f : "there";
        var days = Math.Max(1, (int)(DateTime.UtcNow - item.ReceivedUtc).TotalDays);
        string? body = null;
        if (claude.Enabled)
            body = await claude.TextAsync("inbox-nudge",
                "You write a short, polite follow-up email for a US IT bench-sales recruiter who got no reply. Plain text, under 80 words, " +
                "greet the recipient by first name, refer to the earlier email in one line, ask one clear question. Never invent facts. " +
                "No subject and no signature (the app adds it).",
                $"Recipient first name: {first}\nDays since sent: {days}\nEarlier email, subject \"{item.Subject}\":\n{InboxTriage.Cut(item.Body, 2500)}" +
                (string.IsNullOrWhiteSpace(input.Instructions) ? "" : "\nRecruiter's instructions: " + InboxTriage.Cut(input.Instructions.Trim(), 500)),
                400, fast: false, ct: ct);
        var engine = body is null ? "rules" : "claude";
        body ??= item.Subject.StartsWith("Submission", StringComparison.OrdinalIgnoreCase)
            ? $"Hi {first},\n\nJust following up on the profile I sent {(days == 1 ? "yesterday" : $"{days} days ago")} (below). Did you get a chance to review it? " +
              "Happy to share anything else the client needs, or set up a call."
            : $"Hi {first},\n\nJust following up on my email below from {(days == 1 ? "yesterday" : $"{days} days ago")}. Could you let me know where this stands?";
        var subject = item.Subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) ? item.Subject : "RE: " + item.Subject;
        return new InboxDraftDto(string.Join(", ", to), InboxTriage.Cut(subject, 255), body.Trim(), item.InboxItemId, engine, engine == "rules" ? claude.Notice : null);
    }

    // =====================================================================================================
    // Tasks, follow-ups and interviews
    // =====================================================================================================

    /// <summary>Your tasks. <paramref name="status"/>: open (default) | closed (last 30 days).</summary>
    [HttpGet("tasks"), RequirePerm("mail.view")]
    public Task<List<InboxTaskDto>> Tasks([FromQuery] string? status, CancellationToken ct)
    {
        var meId = me.RequiredId;
        var q = db.InboxTasks.AsNoTracking().Where(t => t.UserId == meId);
        var since = DateTime.UtcNow.AddDays(-30);
        q = status == "closed" ? q.Where(t => t.Status != "Open" && t.ClosedUtc >= since) : q.Where(t => t.Status == "Open");
        return TaskQuery(q.OrderBy(t => t.DueUtc == null).ThenBy(t => t.DueUtc).ThenByDescending(t => t.CreatedUtc).Take(300)).ToListAsync(ct);
    }

    [HttpPost("tasks"), RequirePerm("mail.view")]
    public async Task<InboxTaskDto> AddTask(InboxTaskInput input, CancellationToken ct)
    {
        var t = new InboxTask { UserId = me.RequiredId };
        await ApplyAsync(t, input, ct);
        db.InboxTasks.Add(t);
        await db.SaveChangesAsync(ct);
        await InboxWorkflow.SyncInterviewAsync(db, t, await ConsultantNameAsync(t.ConsultantId, ct), ct);
        await audit.LogAsync("INBOX_TASK", $"Added {t.Kind.ToLowerInvariant()} \"{t.Title}\"");
        return await TaskQuery(db.InboxTasks.AsNoTracking().Where(x => x.InboxTaskId == t.InboxTaskId)).FirstAsync(ct);
    }

    /// <summary>Edits a task (e.g. sets the interview time and consultant) or closes / reopens it.</summary>
    [HttpPut("tasks/{id:int}"), RequirePerm("mail.view")]
    public async Task<InboxTaskDto> UpdateTask(int id, InboxTaskInput input, CancellationToken ct)
    {
        var t = await db.InboxTasks.FirstOrDefaultAsync(x => x.InboxTaskId == id && x.UserId == me.RequiredId, ct) ?? throw AppException.NotFound("Task");
        await ApplyAsync(t, input, ct);
        await db.SaveChangesAsync(ct);
        await InboxWorkflow.SyncInterviewAsync(db, t, await ConsultantNameAsync(t.ConsultantId, ct), ct);
        return await TaskQuery(db.InboxTasks.AsNoTracking().Where(x => x.InboxTaskId == id)).FirstAsync(ct);
    }

    /// <summary>Emails the interview details to the consultant from your Outlook, with a calendar invite (.ics) attached (FRD-006).</summary>
    [HttpPost("tasks/{id:int}/share"), RequirePerm("mail.view"), EnableRateLimiting("ai")]
    public async Task<InboxTaskDto> Share(int id, InboxShareInput input, CancellationToken ct)
    {
        var t = await db.InboxTasks.Include(x => x.InboxItem).FirstOrDefaultAsync(x => x.InboxTaskId == id && x.UserId == me.RequiredId, ct) ?? throw AppException.NotFound("Task");
        if (t.ConsultantId is not int cid) throw AppException.Bad("VALIDATION", "Choose the consultant", "Pick the consultant this interview is for, then share it.");
        if (t.DueUtc is not DateTime start) throw AppException.Bad("VALIDATION", "Set the interview time", "Add the interview date and time, then share it.");
        var c = await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(x => x.ConsultantId == cid).Select(x => new { x.FullName, x.Email }).FirstOrDefaultAsync(ct)
                ?? throw AppException.NotFound("Consultant");
        if (string.IsNullOrWhiteSpace(c.Email) || !Validator.IsEmail(c.Email))
            throw AppException.Bad("NO_EMAIL", "No email for the consultant", $"Add {c.FullName}'s email on the bench first.");

        var minutes = t.DurationMinutes ?? 30;
        var when = $"{InboxWorkflow.Et(start)} ({minutes} min{(t.InterviewType is { } it ? ", " + it : "")})";
        var body = new StringBuilder($"Hi {c.FullName.Split(' ')[0]},\n\n");
        body.AppendLine(string.IsNullOrWhiteSpace(input.Message) ? "Your interview is scheduled. Details below; the calendar invite is attached." : input.Message.Trim());
        body.AppendLine().AppendLine($"Interview: {t.Title}").AppendLine($"When: {when} (the attached invite shows it in your time zone)");
        if (!string.IsNullOrWhiteSpace(t.Location)) body.AppendLine($"Where / link: {t.Location}");
        if (!string.IsNullOrWhiteSpace(t.Notes)) body.AppendLine($"Notes: {t.Notes}");
        body.AppendLine().AppendLine("Please confirm you can make it, and join a few minutes early.").AppendLine().AppendLine("Thanks,").Append(me.Name);

        var ics = Ics(t.InboxTaskId, t.Title, start, minutes, t.Location, t.Notes, me.Name);
        await sender.SendAsync(me.RequiredId, new OutgoingMail([c.Email.Trim().ToLowerInvariant()], [], InboxTriage.Cut("Interview scheduled: " + t.Title, 255), body.ToString(),
            [new OutgoingAttachment("interview.ics", "text/calendar", Encoding.UTF8.GetBytes(ics))], null), ct);
        t.SharedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("INTERVIEW_SHARE", $"Sent interview details \"{t.Title}\" to {c.FullName}", "Consultant", cid);
        return await TaskQuery(db.InboxTasks.AsNoTracking().Where(x => x.InboxTaskId == id)).FirstAsync(ct);
    }

    /// <summary>
    /// The ready draft behind a reminder or task ("Open draft"). Nothing is sent: the executive reviews it, then sends it from the
    /// app or copies it into Outlook. Reply/Nudge answer in the email's thread; Feedback asks the vendor after an interview;
    /// AskConsultant / Slots / Mock write to the consultant; Forward:Team goes to that team.
    /// </summary>
    [HttpPost("tasks/{id:int}/draft"), RequirePerm("mail.view"), EnableRateLimiting("ai")]
    public async Task<InboxDraftDto> TaskDraft(int id, CancellationToken ct)
    {
        var t = await db.InboxTasks.AsNoTracking().Include(x => x.InboxItem).FirstOrDefaultAsync(x => x.InboxTaskId == id && x.UserId == me.RequiredId, ct)
                ?? throw AppException.NotFound("Task");
        var item = t.InboxItem;
        var c = t.ConsultantId is int cid
            ? await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(x => x.ConsultantId == cid).Select(x => new { x.FullName, x.Email }).FirstOrDefaultAsync(ct)
            : null;
        var cFirst = c?.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "there";
        var vendor = item is null ? null : item.Direction == "In" ? item.FromName ?? item.FromAddress
            : (item.ToAddresses ?? "").Split(',', StringSplitOptions.TrimEntries).FirstOrDefault();
        var vFirst = (item?.Direction == "In" ? item.FromName : item?.FromName)?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "there";
        var re = item is null ? t.Title : item.Subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) ? item.Subject : "RE: " + item.Subject;
        var action = t.Action ?? (item != null ? (item.Direction == "Out" ? "Nudge" : "Reply") : null);
        InboxDraftDto Mail(string to, string subject, string body, int? thread) => new(to, InboxTriage.Cut(subject, 255), body, thread, "rules", null);
        string NeedConsultantEmail() => !string.IsNullOrWhiteSpace(c?.Email) ? c!.Email!
            : throw AppException.Bad("NO_EMAIL", c is null ? "Choose the consultant" : "No email for the consultant",
                c is null ? "Edit the task and pick the consultant first." : $"Add {c.FullName}'s email on the bench first.");

        switch (action)
        {
            case "Reply" or "Nudge" when item != null:
                return await Draft(item.InboxItemId, new InboxDraftInput(t.ConsultantId, null), ct);

            case "Feedback" when item != null:
            {
                var parent = t.ParentTaskId is int pid ? await db.InboxTasks.AsNoTracking().FirstOrDefaultAsync(x => x.InboxTaskId == pid, ct) : null;
                var kind = parent?.InterviewType ?? "interview";
                var when = parent?.DueUtc is { } d ? $" on {InboxWorkflow.Et(d)}" : "";
                var to = item.Direction == "In" ? item.FromAddress : (item.ToAddresses ?? "").Split(',', StringSplitOptions.TrimEntries).First();
                return Mail(to, re, $"Hi {vFirst},\n\nThank you for arranging {(c is null ? "the" : c.FullName + "'s")} {kind}{when}. " +
                    "Could you share the client's feedback and the next steps when you have them?\n\nHappy to set up the next round or share anything else they need.",
                    item.InboxItemId);
            }

            case "AskConsultant":
            {
                var requests = InboxWorkflow.RequestsOf(item?.RequestsJson);
                var list = requests.Count > 0 ? string.Join("\n", requests.Select(r => "  - " + r)) : "  - (see the vendor's request)";
                return Mail(NeedConsultantEmail(), $"Documents needed{(item is null ? "" : $": {item.Subject}")}",
                    $"Hi {cFirst},\n\nThe vendor{(vendor is null ? "" : $" ({vendor})")} needs the following to move your profile forward:\n{list}\n\n" +
                    "Please send them by end of day today so I can complete the submission. Reply to this email with the documents attached.", null);
            }

            case "Slots":
            {
                var slots = t.SlotsJson is { } sj ? InboxWorkflow.SlotsOf(sj) : InboxWorkflow.SlotsOf(item?.SlotsJson);
                var lines = slots.Count > 0 ? string.Join("\n", slots.Select((s, n) => $"  {n + 1}. {InboxWorkflow.Et(s)}"))
                    : "  (the vendor hasn't given times yet; please send me 2–3 times that work for you this week)";
                return Mail(NeedConsultantEmail(), $"Interview slots{(item is null ? "" : $": {item.Subject}")}",
                    $"Hi {cFirst},\n\n{(vendor ?? "The vendor")} would like to schedule {(t.InterviewType is { } it ? "a " + it : "an interview")} with you. The times offered are:\n{lines}\n\n" +
                    "Please reply with the one that works best (or another time if none work), and I'll confirm it.", null);
            }

            case "Mock":
            {
                var parent = t.ParentTaskId is int pid ? await db.InboxTasks.AsNoTracking().FirstOrDefaultAsync(x => x.InboxTaskId == pid, ct) : null;
                return Mail(NeedConsultantEmail(), "Mock interview before your interview",
                    $"Hi {cFirst},\n\nBefore your {parent?.InterviewType ?? "interview"}{(parent?.DueUtc is { } d ? $" on {InboxWorkflow.Et(d)}" : "")}, let's do a short mock session " +
                    "to go over your projects, the role and common questions. Could you share a time that works for you in the next day or two?", null);
            }

            case { } a when a.StartsWith("Forward:", StringComparison.Ordinal) && item != null:
            {
                var team = a["Forward:".Length..];
                var to = triageOptions.Value.TeamEmails.TryGetValue(team, out var addr) ? addr : "";
                return Mail(to, "FW: " + item.Subject,
                    $"Hi {team} team,\n\nPlease see the question below from {item.FromName ?? item.FromAddress} and let me know how to answer.\n\n" +
                    $"----- Original message -----\nFrom: {item.FromName} <{item.FromAddress}>\nSent: {InboxWorkflow.Et(item.ReceivedUtc)}\nSubject: {item.Subject}\n\n{InboxTriage.Cut(item.Body, 6000)}", null);
            }

            default:
                if (item != null) return await Draft(item.InboxItemId, new InboxDraftInput(t.ConsultantId, null), ct);
                throw AppException.Bad("NO_DRAFT", "Nothing to draft", "This task isn't linked to an email.");
        }
    }

    /// <summary>Home page: your open tasks (overdue and the next 7 days) with due times, and upcoming interviews:
    /// yours, plus your team's for Team Leads and everyone's for roles that see all data.</summary>
    [HttpGet("home"), RequirePerm("mail.view")]
    public async Task<InboxHomeDto> Home(CancellationToken ct)
    {
        var meId = me.RequiredId;
        var soon = DateTime.UtcNow.AddDays(7);
        var mine = db.InboxTasks.AsNoTracking().Where(t => t.UserId == meId && t.Status == "Open" && t.Kind != "Interview" && (t.DueUtc == null || t.DueUtc <= soon));
        var tasks = await TaskQuery(mine.OrderBy(t => t.DueUtc == null).ThenBy(t => t.DueUtc).Take(15)).ToListAsync(ct);
        var overdue = await mine.CountAsync(t => t.DueUtc < DateTime.UtcNow, ct);
        var users = me.DataScope == "ALL" ? null
            : await db.UserHierarchy.Where(h => h.AncestorUserId == meId).Select(h => h.DescendantUserId).ToListAsync(ct);
        var from = DateTime.UtcNow.AddHours(-2);
        var iv = db.InboxTasks.AsNoTracking().Where(t => t.Kind == "Interview" && t.Status == "Open" && (t.DueUtc == null || t.DueUtc >= from));
        iv = users is null ? iv : iv.Where(t => t.UserId == meId || users.Contains(t.UserId));
        var interviews = await TaskQuery(iv.OrderBy(t => t.DueUtc == null).ThenBy(t => t.DueUtc).Take(20)).ToListAsync(ct);
        return new InboxHomeDto(tasks, interviews, overdue);
    }

    /// <summary>"Fetch matching profiles": re-matches your recent job emails against the bench, using each consultant's skills
    /// plus what their latest resume shows.</summary>
    [HttpPost("rematch"), RequirePerm("mail.view"), EnableRateLimiting("ai")]
    public async Task<InboxRematchResult> Rematch(CancellationToken ct)
    {
        var (jobs, with, consultants) = await triage.RematchAsync(db, me.RequiredId, ct);
        return new InboxRematchResult(jobs, with, consultants);
    }


    /// <summary>Downloads an email's attachment (e.g. the resume on a submission) straight from Outlook. <paramref name="index"/>: its place in the list.</summary>
    [HttpGet("{id:int}/attachments/{index:int}"), RequirePerm("mail.view")]
    public async Task<IActionResult> Attachment(int id, int index, CancellationToken ct)
    {
        var item = await Own(id, ct);
        var list = InboxTriage.AttachmentsOf(item.AttachmentsJson);
        if (index < 0 || index >= list.Count) throw AppException.NotFound("Attachment");
        var (bytes, name, type) = await triage.DownloadAttachmentAsync(await TokenAsync(ct), item.MessageId, list[index].Id, ct);
        await audit.LogAsync("MAIL_ATTACHMENT", $"Downloaded \"{name}\" from \"{item.Subject}\"");
        return File(bytes, type, name);
    }

    // =====================================================================================================

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var conn = await db.MailConnections.FirstOrDefaultAsync(c => c.UserId == me.RequiredId, ct)
                   ?? throw AppException.Conflict("MAIL_NOT_CONNECTED", "Outlook isn't connected", "Connect Outlook to open attachments.");
        return await sync.AccessTokenAsync(db, auth, conn, ct);
    }

    private async Task<InboxItem> Own(int id, CancellationToken ct) =>
        await db.InboxItems.AsNoTracking().FirstOrDefaultAsync(i => i.InboxItemId == id && i.UserId == me.RequiredId, ct) ?? throw AppException.NotFound("Email");

    private async Task ApplyAsync(InboxTask t, InboxTaskInput input, CancellationToken ct)
    {
        var kind = input.Kind ?? t.Kind;
        var title = (input.Title ?? t.Title).Trim();
        new Validator()
            .Check(InboxTriage.TaskKinds.Contains(kind), "kind", "Kind must be Task, FollowUp or Interview")
            .Check(title.Length is > 0 and <= 300, "title", "Add a title (under 300 characters)")
            .Check(input.DurationMinutes is null or (> 0 and <= 480), "durationMinutes", "Duration must be 1 to 480 minutes")
            .Check((input.Location?.Length ?? 0) <= 500, "location", "Keep the location or link under 500 characters")
            .Check((input.Notes?.Length ?? 0) <= 1000, "notes", "Keep the notes under 1,000 characters")
            .Check(input.Status is null || TaskStatuses.Contains(input.Status), "status", "Status must be Open, Done or Dismissed")
            .Check(string.IsNullOrEmpty(input.InterviewType) || InboxWorkflow.InterviewTypes.Contains(input.InterviewType), "interviewType", "Choose Client call, Technical call, L1, L2, Final or Other")
            .ThrowIfInvalid();
        if (input.InboxItemId is int iid && t.InboxItemId != iid)
        {
            if (!await db.InboxItems.AnyAsync(i => i.InboxItemId == iid && i.UserId == me.RequiredId, ct)) throw AppException.NotFound("Email");
            t.InboxItemId = iid;
        }
        if (input.ConsultantId is int cid && cid != t.ConsultantId)
        {
            // Only consultants you can see on the bench (team filter applies).
            if (!await db.Consultants.AnyAsync(c => c.ConsultantId == cid && c.Status != "Inactive", ct)) throw AppException.NotFound("Consultant");
            t.ConsultantId = cid;
        }
        t.Kind = kind;
        t.Title = title;
        if (input.DueUtc is DateTime due) t.DueUtc = due.ToUniversalTime();
        if (input.DurationMinutes is int d) t.DurationMinutes = d;
        if (input.Location != null) t.Location = input.Location.Trim() is { Length: > 0 } l ? l : null;
        if (input.Notes != null) t.Notes = input.Notes.Trim() is { Length: > 0 } n ? n : null;
        if (input.Status is { } s && s != t.Status) { t.Status = s; t.ClosedUtc = s == "Open" ? null : DateTime.UtcNow; }
        if (input.InterviewType != null) t.InterviewType = input.InterviewType.Length == 0 ? null : input.InterviewType;
        if (input.MockSession is bool mock) t.MockSession = mock;
    }

    private Task<string?> ConsultantNameAsync(int? id, CancellationToken ct) => id is int cid
        ? db.Consultants.IgnoreQueryFilters().Where(c => c.ConsultantId == cid).Select(c => (string?)c.FullName).FirstOrDefaultAsync(ct)
        : Task.FromResult<string?>(null);

    private IQueryable<InboxTaskDto> TaskQuery(IQueryable<InboxTask> q) => q.Select(t => new InboxTaskDto(
        t.InboxTaskId, t.InboxItemId, t.InboxItem != null ? t.InboxItem.Subject : null, t.Kind, t.Title, t.DueUtc, t.DurationMinutes, t.ConsultantId,
        t.ConsultantId == null ? null : db.Consultants.IgnoreQueryFilters().Where(c => c.ConsultantId == t.ConsultantId).Select(c => c.FullName).FirstOrDefault(),
        t.ConsultantId == null ? null : db.Consultants.IgnoreQueryFilters().Where(c => c.ConsultantId == t.ConsultantId).Select(c => c.Email).FirstOrDefault(),
        t.Location, t.Notes, t.SharedUtc, t.Status, t.Auto, t.Action, t.InterviewType, t.MockSession,
        t.SlotsJson == null ? null : InboxWorkflow.SlotsOf(t.SlotsJson), t.ParentTaskId, t.Sequence,
        db.Users.Where(u => u.UserId == t.UserId).Select(u => u.FullName).FirstOrDefault()));

    private static InboxItemDto ToDto(InboxItem i, int openTasks, DateTime? answeredUtc = null) => new(i.InboxItemId, i.ReceivedUtc, i.FromAddress, i.FromName, i.Subject, i.Summary,
        i.Category, i.Priority, i.PriorityReason, i.NeedsReply, i.Status, i.Engine, i.HasAttachments, InboxTriage.JobOf(i.JobJson), InboxTriage.MatchesOf(i.MatchesJson),
        openTasks, i.RequirementId, i.Direction, i.ToAddresses, answeredUtc,
        InboxTriage.AttachmentsOf(i.AttachmentsJson).Select((a, n) => new InboxAttachmentDto(n, a.Name, a.Size, a.ContentType)).ToList(),
        i.ConsultantId, null, InboxWorkflow.RequestsOf(i.RequestsJson), i.Team, InboxWorkflow.SlotsOf(i.SlotsJson));

    private Task<DateTime?> AnsweredAsync(InboxItem i, CancellationToken ct) => i.ConversationId is null
        ? Task.FromResult<DateTime?>(null)
        : db.InboxItems.AsNoTracking().Where(r => r.UserId == i.UserId && r.ConversationId == i.ConversationId && r.Direction != i.Direction && r.ReceivedUtc > i.ReceivedUtc)
            .MaxAsync(r => (DateTime?)r.ReceivedUtc, ct);

    /// <summary>A minimal iCalendar invite (RFC 5545) the consultant's calendar can add.</summary>
    private static string Ics(int id, string title, DateTime startUtc, int minutes, string? location, string? notes, string organizer)
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Collaborate-Bench//AI Inbox//EN\r\nMETHOD:PUBLISH\r\nBEGIN:VEVENT\r\n");
        sb.Append($"UID:interview-{id}-{startUtc:yyyyMMddHHmm}@collaborate-bench\r\nDTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}\r\n");
        sb.Append($"DTSTART:{startUtc:yyyyMMdd'T'HHmmss'Z'}\r\nDTEND:{startUtc.AddMinutes(minutes):yyyyMMdd'T'HHmmss'Z'}\r\n");
        sb.Append($"SUMMARY:{Esc("Interview: " + title)}\r\n");
        if (!string.IsNullOrWhiteSpace(location)) sb.Append($"LOCATION:{Esc(location)}\r\n");
        sb.Append($"DESCRIPTION:{Esc((notes ?? "") + (string.IsNullOrWhiteSpace(location) ? "" : "\nJoin: " + location) + "\nScheduled by " + organizer)}\r\n");
        sb.Append("BEGIN:VALARM\r\nTRIGGER:-PT15M\r\nACTION:DISPLAY\r\nDESCRIPTION:Interview in 15 minutes\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        return sb.ToString();
    }
}
