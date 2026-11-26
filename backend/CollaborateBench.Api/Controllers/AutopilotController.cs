using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Controllers;

/// <summary>
/// Bench sales Autopilot: the queue of opportunities the AI found and prepared, recruiter approval, vendor replies, and settings.
/// Recruiters see their own consultants' items, Team Leads their team's, managers everyone's.
/// </summary>
[ApiController, Route("api/autopilot")]
public sealed class AutopilotController(AppDbContext db, DataService data, AutopilotService autopilot, AutopilotWorker worker, ClaudeClient claude,
    ICurrentUser me, AuditService audit, IOptions<AutopilotOptions> alertOptions) : ControllerBase
{
    private IQueryable<AutopilotItem> Visible()
    {
        var users = data.VisibleUsers().Select(u => u.UserId);
        return db.AutopilotItems.Where(i => users.Contains(i.OwnerUserId));
    }

    /// <summary>
    /// Loads an item for changing. Visibility is checked separately: <see cref="Visible"/> contains a no-tracking subquery, and EF Core
    /// applies no-tracking to the whole query, so an entity loaded through it would be detached and its changes silently lost.
    /// </summary>
    private async Task<AutopilotItem> LoadForEditAsync(int id, CancellationToken ct = default)
    {
        if (!await Visible().AnyAsync(i => i.AutopilotItemId == id, ct)) throw AppException.NotFound("Autopilot item");
        return await db.AutopilotItems.FirstAsync(i => i.AutopilotItemId == id, ct);
    }

    /// <summary>Counts for the Autopilot page header and the sidebar.</summary>
    [HttpGet("summary"), RequirePerm("pipeline.view")]
    public async Task<AutopilotSummary> Summary()
    {
        var s = await autopilot.SettingsAsync();
        var counts = await Visible().GroupBy(i => i.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        int N(string k) => counts.GetValueOrDefault(k);
        var week = DateTime.UtcNow.AddDays(-7);
        var visible = data.VisibleUsers().Select(u => u.UserId);
        return new AutopilotSummary(s.Enabled, s.LastRunUtc, s.LastRunSummary, N("Found"), N("NeedsResume"), N("Prepared") + N("RtrDeclined") + N("RtrExpired"),
            N("RtrSent"), await Visible().CountAsync(i => i.Status == "Submitted" && i.SubmittedUtc >= week),
            await db.VendorEmails.CountAsync(e => visible.Contains(e.UserId) && e.Intent != null && !e.IntentHandled), N("Failed"));
    }

    /// <summary>Items by group: approval (default), rtr, submitted, attention (needs resume / failed), skipped, found.</summary>
    [HttpGet("items"), RequirePerm("pipeline.view")]
    public async Task<List<AutopilotItemDto>> Items([FromQuery] string? group)
    {
        string[] statuses = (group ?? "approval") switch
        {
            "rtr" => ["RtrSent", "RtrApproved"], "submitted" => ["Submitted"], "attention" => ["NeedsResume", "Failed"],
            "skipped" => ["Skipped"], "found" => ["Found"], _ => ["Prepared", "RtrDeclined", "RtrExpired"],
        };
        var items = await Visible().AsNoTracking().Where(i => statuses.Contains(i.Status))
            .OrderByDescending(i => i.Status == "Submitted" ? i.SubmittedUtc : i.UpdatedUtc).Take(200).ToListAsync();
        return await ToDtosAsync(items);
    }

    /// <summary>Approve a prepared submission: saves the final values and emails the consultant the RTR link with the tailored resume.</summary>
    [HttpPost("items/{id:int}/approve"), RequirePerm("sub.create"), EnableRateLimiting("ai")]
    public async Task<AutopilotItemDto> Approve(int id, AutopilotApproveInput input, CancellationToken ct)
    {
        var item = await LoadForEditAsync(id, ct);
        await autopilot.ApproveAsync(item, input, me.RequiredId, me.Name, ct);
        return (await ToDtosAsync([item]))[0];
    }

    /// <summary>Skip an opportunity. It won't be suggested again for this consultant.</summary>
    [HttpPost("items/{id:int}/skip"), RequirePerm("sub.create")]
    public async Task<IActionResult> Skip(int id)
    {
        var item = await LoadForEditAsync(id);
        if (item.Status is "Submitted" or "RtrApproved") throw AppException.Conflict("WRONG_STATUS", "Can't skip", "This one is already being submitted.");
        item.Status = "Skipped"; item.Error = $"Skipped by {me.Name}"; item.UpdatedUtc = DateTime.UtcNow;
        await db.RtrRequests.Where(r => r.AutopilotItemId == id && r.Status == "Pending").ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "Expired"));
        await db.SaveChangesAsync();
        await audit.LogAsync("AUTOPILOT_SKIP", $"Skipped Autopilot suggestion {item.JobTitle}", "Consultant", item.ConsultantId);
        return NoContent();
    }

    /// <summary>Try again after a failure or a skip: a failed submission is resent; anything else is prepared again.</summary>
    [HttpPost("items/{id:int}/retry"), RequirePerm("sub.create")]
    public async Task<IActionResult> Retry(int id)
    {
        var item = await LoadForEditAsync(id);
        if (item.Status is not ("Failed" or "Skipped" or "NeedsResume")) throw AppException.Conflict("WRONG_STATUS", "Nothing to retry", $"This one is {AutopilotService.Label(item.Status)}.");
        var rtrApproved = await db.RtrRequests.AnyAsync(r => r.AutopilotItemId == id && r.Status == "Approved");
        item.Status = rtrApproved && item.SubmissionId is null ? "RtrApproved" : item.TailoredResumeId is null ? "Found" : "Prepared";
        item.Error = null; item.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        worker.Kick(fullRun: item.Status == "Found");
        return NoContent();
    }

    /// <summary>Vendor replies the Autopilot read and classified, waiting for a recruiter to act.</summary>
    [HttpGet("replies"), RequirePerm("pipeline.view")]
    public async Task<List<VendorReplyDto>> Replies()
    {
        var visible = data.VisibleUsers().Select(u => u.UserId);
        var rows = await db.VendorEmails.AsNoTracking().Where(e => visible.Contains(e.UserId) && e.Intent != null && !e.IntentHandled)
            .OrderByDescending(e => e.SentUtc).Take(100)
            .Select(e => new { e.VendorEmailId, e.SentUtc, Vendor = e.Vendor.Name, e.FromAddress, e.FromName, e.Subject, e.Preview, e.Intent, e.IntentSummary,
                e.SubmissionId, e.ConsultantId, e.UserId, UserName = e.User.FullName }).ToListAsync();
        var subIds = rows.Where(r => r.SubmissionId != null).Select(r => r.SubmissionId!.Value).Distinct().ToList();
        var subs = subIds.Count == 0 ? [] : await db.Submissions.AsNoTracking().Where(s => subIds.Contains(s.SubmissionId))
            .Select(s => new { s.SubmissionId, s.Stage, s.Requirement.Title, Consultant = s.Consultant.FullName }).ToDictionaryAsync(s => s.SubmissionId);
        var cIds = rows.Where(r => r.ConsultantId != null).Select(r => r.ConsultantId!.Value).Distinct().ToList();
        var names = cIds.Count == 0 ? [] : await db.Consultants.IgnoreQueryFilters().Where(c => cIds.Contains(c.ConsultantId)).ToDictionaryAsync(c => c.ConsultantId, c => c.FullName);
        return rows.Select(r =>
        {
            var s = r.SubmissionId is int sid ? subs.GetValueOrDefault(sid) : null;
            return new VendorReplyDto(r.VendorEmailId, r.SentUtc, r.Vendor, r.FromAddress, r.FromName, r.Subject, r.Preview, r.Intent!, r.IntentSummary,
                s?.SubmissionId, r.ConsultantId, s?.Consultant ?? (r.ConsultantId is int c ? names.GetValueOrDefault(c) : null), s?.Title, s?.Stage, r.UserId, r.UserName);
        }).ToList();
    }

    /// <summary>Marks a reply as dealt with (after moving the stage, replying or deciding no action is needed).</summary>
    [HttpPost("replies/{id:int}/handled"), RequirePerm("pipeline.view")]
    public async Task<IActionResult> Handled(int id)
    {
        var visible = data.VisibleUsers().Select(u => u.UserId);
        var n = await db.VendorEmails.Where(e => e.VendorEmailId == id && visible.Contains(e.UserId)).ExecuteUpdateAsync(u => u.SetProperty(e => e.IntentHandled, true));
        return n == 0 ? throw AppException.NotFound("Email") : NoContent();
    }

    /// <summary>Autopilot settings (everyone can read; Team Lead and above change them).</summary>
    [HttpGet("settings"), RequirePerm("pipeline.view")]
    public async Task<AutopilotSettingsDto> Settings()
    {
        var s = await autopilot.SettingsAsync();
        return new AutopilotSettingsDto(s.Enabled, s.MinScore, s.MaxNewPerConsultant, s.MaxPreparePerRun, s.IncludeExternalJobs, s.AutoFollowUp, s.MaxAutoFollowUps,
            s.ClassifyReplies, s.RunEveryMinutes, s.LastRunUtc, s.LastRunSummary, claude.Enabled, me.Has("autopilot.manage"), s.EmailNotifications);
    }

    [HttpPut("settings"), RequirePerm("autopilot.manage")]
    public async Task<AutopilotSettingsDto> SaveSettings(AutopilotSettingsInput input)
    {
        new Validator()
            .Check(input.MinScore is >= 40 and <= 100, "minScore", "Minimum match between 40% and 100%")
            .Check(input.MaxNewPerConsultant is >= 1 and <= 10, "maxNewPerConsultant", "Between 1 and 10 new jobs per consultant per day")
            .Check(input.MaxPreparePerRun is >= 1 and <= 100, "maxPreparePerRun", "Between 1 and 100 per run (each one is an AI call)")
            .Check(input.MaxAutoFollowUps is >= 0 and <= 5, "maxAutoFollowUps", "Between 0 and 5 follow-ups per thread")
            .Check(input.RunEveryMinutes is >= 10 and <= 1440, "runEveryMinutes", "Between 10 minutes and 24 hours")
            .ThrowIfInvalid();
        var s = await autopilot.SettingsAsync();
        var turnedOn = input.Enabled && !s.Enabled;
        s.Enabled = input.Enabled; s.MinScore = input.MinScore; s.MaxNewPerConsultant = input.MaxNewPerConsultant; s.MaxPreparePerRun = input.MaxPreparePerRun;
        // Follow-ups are never sent automatically (the executive approves each draft), so the old switch always stays off.
        s.IncludeExternalJobs = input.IncludeExternalJobs; s.AutoFollowUp = false; s.MaxAutoFollowUps = input.MaxAutoFollowUps;
        s.ClassifyReplies = input.ClassifyReplies; s.RunEveryMinutes = input.RunEveryMinutes; s.EmailNotifications = input.EmailNotifications;
        await db.SaveChangesAsync();
        await audit.LogAsync("AUTOPILOT_SETTINGS", $"Autopilot {(s.Enabled ? "on" : "off")}: min match {s.MinScore}%, {s.MaxNewPerConsultant}/consultant/day");
        if (turnedOn) worker.Kick(fullRun: true);
        return await Settings();
    }

    /// <summary>Your Autopilot email alerts: which events you get, and where they come from.</summary>
    [HttpGet("notifications"), RequirePerm("pipeline.view")]
    public async Task<NotificationPrefsDto> MyAlerts() => await AlertsDtoAsync(me.RequiredId);

    [HttpPut("notifications"), RequirePerm("pipeline.view")]
    public async Task<NotificationPrefsDto> SaveMyAlerts(NotificationPrefsInput input)
    {
        var meId = me.RequiredId;
        var p = await db.NotificationPrefs.FirstOrDefaultAsync(x => x.UserId == meId);
        if (p is null) db.NotificationPrefs.Add(p = new NotificationPrefs { UserId = meId });
        p.Enabled = input.Enabled; p.Approvals = input.Approvals; p.VendorReplies = input.VendorReplies;
        p.RtrUpdates = input.RtrUpdates; p.Problems = input.Problems; p.FollowUps = input.FollowUps;
        await db.SaveChangesAsync();
        await audit.LogAsync("ALERTS_UPDATE", $"Autopilot email alerts {(p.Enabled ? "on" : "off")}");
        return await AlertsDtoAsync(meId);
    }

    private async Task<NotificationPrefsDto> AlertsDtoAsync(int userId)
    {
        var p = await db.NotificationPrefs.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId) ?? new NotificationPrefs { UserId = userId };
        var s = await autopilot.SettingsAsync();
        var fromBox = alertOptions.Value.NotifyFromEmail?.Trim();
        var shared = string.IsNullOrEmpty(fromBox) ? null
            : await db.MailConnections.AsNoTracking().Where(m => m.Mailbox == fromBox && m.Status == "Active").Select(m => m.Mailbox).FirstOrDefaultAsync();
        var own = await db.MailConnections.AsNoTracking().AnyAsync(m => m.UserId == userId && m.Status == "Active");
        string? reason = !s.EmailNotifications ? "Email alerts are switched off for the company in Autopilot settings."
            : shared is null && !own ? "Connect your Outlook (Vendor Emails → Connect Outlook) so alerts can be sent to you." : null;
        var last = await db.AutopilotNotifications.Where(n => n.UserId == userId && n.Status == "Sent").MaxAsync(n => (DateTime?)n.SentUtc);
        return new NotificationPrefsDto(p.Enabled, p.Approvals, p.VendorReplies, p.RtrUpdates, p.Problems, p.FollowUps, shared, reason is null, reason, last);
    }

    /// <summary>Runs the Autopilot now in the background (find, prepare, follow up, read replies). Check the summary for the result.</summary>
    [HttpPost("run"), RequirePerm("autopilot.manage")]
    public async Task<IActionResult> RunNow()
    {
        if (!(await autopilot.SettingsAsync()).Enabled) throw AppException.Conflict("AUTOPILOT_OFF", "Autopilot is off", "Turn the Autopilot on in Settings first.");
        worker.Kick(fullRun: true);
        await audit.LogAsync("AUTOPILOT_RUN", "Started an Autopilot run");
        return Accepted();
    }

    private async Task<List<AutopilotItemDto>> ToDtosAsync(List<AutopilotItem> items)
    {
        var cIds = items.Select(i => i.ConsultantId).Distinct().ToList();
        var cons = await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(c => cIds.Contains(c.ConsultantId))
            .Select(c => new { c.ConsultantId, c.FullName, c.Title, c.Email }).ToDictionaryAsync(c => c.ConsultantId);
        var uIds = items.Select(i => i.OwnerUserId).Concat(items.Where(i => i.ApprovedByUserId != null).Select(i => i.ApprovedByUserId!.Value)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => uIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName);
        var tIds = items.Where(i => i.TailoredResumeId != null).Select(i => i.TailoredResumeId!.Value).ToList();
        var tailored = await db.TailoredResumes.AsNoTracking().Where(t => tIds.Contains(t.TailoredResumeId))
            .Select(t => new { t.TailoredResumeId, t.Warnings, t.Gaps }).ToDictionaryAsync(t => t.TailoredResumeId);
        var iIds = items.Select(i => i.AutopilotItemId).ToList();
        var rtrs = (await db.RtrRequests.AsNoTracking().Where(r => iIds.Contains(r.AutopilotItemId)).ToListAsync())
            .GroupBy(r => r.AutopilotItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedUtc).First());
        return items.Select(i =>
        {
            var c = cons.GetValueOrDefault(i.ConsultantId);
            var t = i.TailoredResumeId is int tid ? tailored.GetValueOrDefault(tid) : null;
            var r = rtrs.GetValueOrDefault(i.AutopilotItemId);
            return new AutopilotItemDto(i.AutopilotItemId, i.Status, AutopilotService.Label(i.Status), i.ConsultantId, c?.FullName ?? "", c?.Title ?? "", c?.Email,
                i.OwnerUserId, users.GetValueOrDefault(i.OwnerUserId) ?? "", i.RequirementId, i.ExternalJobId, i.JobTitle, i.Client, i.Vendor, i.Score, i.Why,
                i.TailoredResumeId, t?.Warnings.Count ?? 0, t?.Gaps ?? [], i.BillRate, i.SubmitRate, i.PayRate, i.ContractType, i.VendorEmail, i.EmailSubject,
                i.EmailBody, i.Error, i.ApprovedByUserId is int a ? users.GetValueOrDefault(a) : null, i.ApprovedUtc, i.SubmissionId, i.SubmittedUtc,
                r is null ? null : new RtrInfo(r.Status, r.CreatedUtc, r.ExpiresUtc, r.RespondedUtc, r.SignedName, r.DeclineReason), i.CreatedUtc, i.UpdatedUtc);
        }).ToList();
    }
}

/// <summary>
/// The consultant's Right to Represent page (public, no sign-in). The one-time link is the only key: it shows the minimum
/// needed to decide (role, client, vendor, their own pay rate), and records the answer with time, IP and browser.
/// </summary>
[ApiController, Route("api/rtr"), AllowAnonymous]
public sealed class RtrController(AppDbContext db, AutopilotService autopilot, AutopilotWorker worker, IOptions<AutopilotOptions> options) : ControllerBase
{
    /// <summary>What the consultant is asked to approve.</summary>
    [HttpGet("{token}"), EnableRateLimiting("login")]
    public async Task<RtrView> Get(string token, CancellationToken ct)
    {
        var rtr = await autopilot.FindRtrAsync(token, ct) ?? throw NotFoundLink();
        var item = await db.AutopilotItems.AsNoTracking().FirstAsync(i => i.AutopilotItemId == rtr.AutopilotItemId, ct);
        var consultant = await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(c => c.ConsultantId == item.ConsultantId).Select(c => c.FullName).FirstAsync(ct);
        var recruiter = await db.Users.AsNoTracking().Where(u => u.UserId == item.ApprovedByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "Your recruiter";
        var location = item.RequirementId is int rid ? await db.Requirements.Where(r => r.RequirementId == rid).Select(r => r.Location).FirstOrDefaultAsync(ct)
            : await db.ExternalJobs.Where(j => j.ExternalJobId == item.ExternalJobId).Select(j => j.Location).FirstOrDefaultAsync(ct);
        return new RtrView(rtr.Status, consultant.Split(' ')[0], item.JobTitle, item.Client, item.Vendor == "Direct client" ? null : item.Vendor, location,
            item.ContractType, item.PayRate, rtr.Statement, recruiter, options.Value.CompanyName, rtr.ExpiresUtc, rtr.ExpiresUtc < DateTime.UtcNow);
    }

    /// <summary>Approve (with typed full name) or decline. On approval the submission is sent to the vendor within a minute or two.</summary>
    [HttpPost("{token}"), EnableRateLimiting("login")]
    public async Task<object> Respond(string token, RtrResponseInput input, CancellationToken ct)
    {
        var rtr = await autopilot.FindRtrAsync(token, ct) ?? throw NotFoundLink();
        if (input.Decision is not ("approve" or "decline")) throw AppException.Bad("VALIDATION", "Choose approve or decline", "Pick whether you approve this submission.");
        var approve = input.Decision == "approve";
        await autopilot.RespondRtrAsync(rtr, approve, input.SignedName, input.Reason, HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), ct);
        if (approve) worker.Kick();
        return new { status = approve ? "Approved" : "Declined" };
    }

    private static AppException NotFoundLink() =>
        new(404, "RTR_NOT_FOUND", "Link not valid", "This approval link isn't valid. It may have been replaced by a newer one; check your latest email or contact your recruiter.");
}
