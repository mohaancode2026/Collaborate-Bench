using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Services;

/// <summary>"FollowUps" section of appsettings.</summary>
public sealed class FollowUpOptions
{
    /// <summary>An email you sent that gets no vendor reply for this many hours gets an automatic reminder. 0 = no automatic reminders.</summary>
    public int AutoAfterHours { get; set; } = 48;
    /// <summary>Threads quiet for longer than this are considered dead and don't get automatic reminders.</summary>
    public int AutoMaxDays { get; set; } = 14;
}

/// <summary>Keeps reminders in step with the mailbox: closes them when the thread moves on, and creates automatic ones.</summary>
public static class FollowUps
{
    public static string Key(string? conversationId, int emailId) => conversationId ?? $"#{emailId}";

    /// <returns>(created, closed)</returns>
    public static async Task<(int Created, int Closed)> ReconcileAsync(AppDbContext db, int userId, FollowUpOptions o, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(o.AutoMaxDays, 30) - 30);
        var emails = await db.VendorEmails.AsNoTracking().Where(e => e.UserId == userId && e.SentUtc >= since)
            .Select(e => new { e.VendorEmailId, e.ConversationId, e.Direction, e.SentUtc }).ToListAsync(ct);
        var latest = emails.GroupBy(e => Key(e.ConversationId, e.VendorEmailId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.SentUtc).ThenByDescending(e => e.VendorEmailId).First());
        var sentAt = emails.ToDictionary(e => e.VendorEmailId, e => e.SentUtc);

        var open = await db.FollowUpReminders.Where(r => r.UserId == userId && r.Status == "Open").ToListAsync(ct);
        var closed = 0;
        foreach (var r in open)
        {
            // A newer email in the same thread means the wait is over: the vendor answered, or you already followed up.
            if (!latest.TryGetValue(r.ConversationKey, out var last) || last.VendorEmailId == r.VendorEmailId) continue;
            if (sentAt.TryGetValue(r.VendorEmailId, out var anchor) && last.SentUtc <= anchor) continue;
            r.Status = last.Direction == "In" ? "Replied" : "Done";
            r.ClosedUtc = DateTime.UtcNow;
            closed++;
        }

        var created = 0;
        if (o.AutoAfterHours > 0)
        {
            var now = DateTime.UtcNow;
            var wait = TimeSpan.FromHours(o.AutoAfterHours);
            var openKeys = open.Where(r => r.Status == "Open").Select(r => r.ConversationKey).ToHashSet();
            var candidates = latest.Where(kv => kv.Value.Direction == "Out" && kv.Value.SentUtc <= now - wait && kv.Value.SentUtc >= now.AddDays(-o.AutoMaxDays)
                                                && !openKeys.Contains(kv.Key)).ToList();
            var ids = candidates.Select(c => c.Value.VendorEmailId).ToList();
            // Never recreate a reminder the user already finished or dismissed for that same email.
            var had = ids.Count == 0 ? [] : (await db.FollowUpReminders.Where(r => r.UserId == userId && ids.Contains(r.VendorEmailId))
                .Select(r => r.VendorEmailId).ToListAsync(ct)).ToHashSet();
            foreach (var (key, last) in candidates.Where(c => !had.Contains(c.Value.VendorEmailId)))
            {
                db.FollowUpReminders.Add(new FollowUpReminder
                {
                    UserId = userId, VendorEmailId = last.VendorEmailId, ConversationKey = key, DueUtc = last.SentUtc + wait, Auto = true,
                });
                created++;
            }
        }
        if (created + closed > 0) await db.SaveChangesAsync(ct);
        return (created, closed);
    }
}
