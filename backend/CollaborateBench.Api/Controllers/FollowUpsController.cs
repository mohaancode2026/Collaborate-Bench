using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Controllers;

/// <summary>Follow-up reminders on vendor emails that are waiting for an answer.</summary>
[ApiController, Route("api/mail/reminders")]
public sealed class FollowUpsController(AppDbContext db, DataService data, ICurrentUser me, AuditService audit, IOptions<FollowUpOptions> options) : ControllerBase
{
    private static readonly string[] UserStatuses = ["Open", "Done", "Dismissed"];

    /// <summary>Your follow-up reminders, soonest first. Brings them up to date with the mailbox first (closes answered threads, adds automatic ones).</summary>
    /// <param name="status">open (default) or closed (the last 30 days).</param>
    /// <param name="scope">mine (default) or team, so leads can see which follow-ups are overdue across their team.</param>
    [HttpGet, RequirePerm("mail.view")]
    public async Task<List<ReminderDto>> List([FromQuery] string? status, [FromQuery] string? scope)
    {
        var meId = me.RequiredId;
        await FollowUps.ReconcileAsync(db, meId, options.Value);
        var q = db.FollowUpReminders.AsNoTracking().AsQueryable();
        if (scope is "team" or "all" && me.DataScope != "OWN")
        {
            var visible = data.VisibleUsers().Select(u => u.UserId);
            q = q.Where(r => visible.Contains(r.UserId));
            if (scope == "team") q = q.Where(r => db.UserHierarchy.Any(h => h.AncestorUserId == meId && h.DescendantUserId == r.UserId));
        }
        else q = q.Where(r => r.UserId == meId);
        var closed = status == "closed";
        var since = DateTime.UtcNow.AddDays(-30);
        q = closed ? q.Where(r => r.Status != "Open" && r.ClosedUtc >= since) : q.Where(r => r.Status == "Open");

        q = closed ? q.OrderByDescending(r => r.ClosedUtc) : q.OrderBy(r => r.DueUtc);
        var rows = await q.Take(500).Select(r => new
        {
            r.ReminderId, r.Status, r.DueUtc, r.Note, r.Auto, r.CreatedUtc, r.ClosedUtc, r.UserId, UserName = r.VendorEmail.User.FullName,
            r.VendorEmailId, r.VendorEmail.Subject, r.VendorEmail.Preview, Vendor = r.VendorEmail.Vendor.Name, r.VendorEmail.VendorId,
            r.VendorEmail.ToAddresses, r.VendorEmail.SentUtc, r.VendorEmail.ConsultantId,
        }).ToListAsync();
        var cids = rows.Where(r => r.ConsultantId != null).Select(r => r.ConsultantId!.Value).Distinct().ToList();
        var names = cids.Count == 0 ? new Dictionary<int, string>()
            : await db.Consultants.IgnoreQueryFilters().Where(c => cids.Contains(c.ConsultantId)).ToDictionaryAsync(c => c.ConsultantId, c => c.FullName);
        var now = DateTime.UtcNow;
        return rows.Select(r => new ReminderDto(r.ReminderId, r.Status, r.DueUtc, r.Status == "Open" && r.DueUtc <= now, r.Note, r.Auto, r.CreatedUtc, r.ClosedUtc,
            r.VendorEmailId, r.Subject, r.Preview, r.Vendor, r.VendorId, r.ToAddresses, r.SentUtc, Math.Max(0, (int)(now - r.SentUtc).TotalDays),
            r.ConsultantId, r.ConsultantId is int c ? names.GetValueOrDefault(c) : null, r.UserId, r.UserName, r.UserId == meId)).ToList();
    }

    /// <summary>For the sidebar badge: your open reminders that are due now.</summary>
    [HttpGet("count"), RequirePerm("mail.view")]
    public async Task<ReminderCount> Count()
    {
        var meId = me.RequiredId;
        await FollowUps.ReconcileAsync(db, meId, options.Value);
        var now = DateTime.UtcNow;
        var open = db.FollowUpReminders.Where(r => r.UserId == meId && r.Status == "Open");
        return new ReminderCount(await open.CountAsync(r => r.DueUtc <= now), await open.CountAsync());
    }

    /// <summary>Remind me about this email. One open reminder per thread: setting another moves the existing one.</summary>
    [HttpPost, RequirePerm("mail.view")]
    public async Task<object> Create(ReminderInput input)
    {
        var meId = me.RequiredId;
        var email = await db.VendorEmails.AsNoTracking().FirstOrDefaultAsync(e => e.VendorEmailId == input.EmailId && e.UserId == meId)
                    ?? throw AppException.NotFound("Email");
        Check(input.DueUtc, input.Note);
        var key = FollowUps.Key(email.ConversationId, email.VendorEmailId);
        var r = await db.FollowUpReminders.FirstOrDefaultAsync(x => x.UserId == meId && x.Status == "Open" && x.ConversationKey == key);
        if (r is null) db.FollowUpReminders.Add(r = new FollowUpReminder { UserId = meId, ConversationKey = key });
        r.VendorEmailId = email.VendorEmailId;
        r.DueUtc = DateTime.SpecifyKind(input.DueUtc, DateTimeKind.Utc);
        r.Note = string.IsNullOrWhiteSpace(input.Note) ? r.Note : input.Note.Trim();   // moving a reminder keeps its note
        r.Auto = false;
        await db.SaveChangesAsync();
        await audit.LogAsync("REMINDER_SET", $"Follow-up reminder for \"{email.Subject}\" on {r.DueUtc:MMM d, HH:mm} UTC");
        return new { id = r.ReminderId };
    }

    /// <summary>Snooze (new due date), edit the note, mark done, dismiss or reopen. Only your own reminders.</summary>
    [HttpPatch("{id:int}"), RequirePerm("mail.view")]
    public async Task<object> Update(int id, ReminderUpdate input)
    {
        var r = await db.FollowUpReminders.FirstOrDefaultAsync(x => x.ReminderId == id && x.UserId == me.RequiredId) ?? throw AppException.NotFound("Reminder");
        if (input.Status != null && !UserStatuses.Contains(input.Status))
            throw AppException.Bad("VALIDATION", "Invalid status", "Status must be Open, Done or Dismissed.");
        if (input.DueUtc is DateTime due) { Check(due, input.Note ?? r.Note); r.DueUtc = DateTime.SpecifyKind(due, DateTimeKind.Utc); }
        if (input.Note != null) { Check(r.DueUtc, input.Note, checkDue: false); r.Note = input.Note.Trim().Length == 0 ? null : input.Note.Trim(); }
        if (input.Status is { } st && st != r.Status)
        {
            if (st == "Open" && await db.FollowUpReminders.AnyAsync(x => x.UserId == r.UserId && x.Status == "Open" && x.ConversationKey == r.ConversationKey && x.ReminderId != r.ReminderId))
                throw AppException.Conflict("REMINDER_EXISTS", "Already has a reminder", "This thread already has an open reminder.");
            r.Status = st;
            r.ClosedUtc = st == "Open" ? null : DateTime.UtcNow;
            if (st == "Open" && r.DueUtc < DateTime.UtcNow) r.DueUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return new { id = r.ReminderId, status = r.Status, dueUtc = r.DueUtc };
    }

    private static void Check(DateTime due, string? note, bool checkDue = true) => new Validator()
        .Check(!checkDue || due >= DateTime.UtcNow.AddMinutes(-5), "dueUtc", "Pick a time in the future")
        .Check(!checkDue || due <= DateTime.UtcNow.AddDays(180), "dueUtc", "Pick a date within the next 6 months")
        .Check((note?.Length ?? 0) <= 500, "note", "Keep the note under 500 characters")
        .ThrowIfInvalid();
}
