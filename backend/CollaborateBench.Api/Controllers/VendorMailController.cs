using System.Text.RegularExpressions;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>Vendor directory (which email domains count as vendors) and the vendor email activity tracked from Outlook.</summary>
[ApiController, Route("api")]
public sealed class VendorMailController(AppDbContext db, DataService data, ICurrentUser me, AuditService audit, MicrosoftAuth ms, MailSync sync, MailSender sender) : ControllerBase
{
    /// <summary>Outgoing mail with no vendor reply in the same thread for this long counts as "awaiting reply".</summary>
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromHours(48);

    // ---------- connection ----------

    /// <summary>Your Outlook connection: configured?, connected?, status, last sync, whether sending is allowed.</summary>
    [HttpGet("mail/connection"), RequirePerm("mail.view")]
    public async Task<MailConnectionDto> Connection()
    {
        var c = await db.MailConnections.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me.RequiredId);
        return new MailConnectionDto(ms.Options.Configured, c != null, c?.Mailbox, c?.Status, c?.LastSyncUtc, c?.LastError,
            await db.Vendors.CountAsync(v => v.IsActive), c != null && MailSync.CanSend(c.GrantedScopes));
    }

    /// <summary>Reads new mail from your Outlook now instead of waiting for the next automatic sync.</summary>
    [HttpPost("mail/sync"), RequirePerm("mail.view"), EnableRateLimiting("ai")]
    public async Task<SyncResult> SyncNow(CancellationToken ct)
    {
        if (!await db.MailConnections.AnyAsync(x => x.UserId == me.RequiredId, ct))
            throw AppException.Conflict("MAIL_NOT_CONNECTED", "Outlook isn't connected", "Connect your Outlook mailbox first.");
        return await sync.SyncAsync(me.RequiredId, ct);
    }

    /// <summary>Stops tracking. Already tracked email summaries stay, as a record of vendor contact.</summary>
    [HttpDelete("mail/connection"), RequirePerm("mail.view")]
    public async Task<IActionResult> Disconnect()
    {
        var n = await db.MailConnections.Where(x => x.UserId == me.RequiredId).ExecuteDeleteAsync();
        sync.Forget(me.RequiredId);
        if (n > 0) await audit.LogAsync("MAIL_DISCONNECT", "Disconnected Outlook. Vendor email tracking stopped.");
        return NoContent();
    }

    // ---------- sending ----------

    private const long MaxAttachmentBytes = 3 * 1024 * 1024;   // Graph's limit for attachments sent in one request is about 3-4 MB
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".bat", ".cmd", ".com", ".msi", ".ps1", ".vbs", ".js", ".jar", ".scr", ".dll", ".reg", ".lnk", ".hta", ".iso" };

    /// <summary>Sends from the user's own Outlook mailbox (multipart form: to, cc, subject, body, replyToEmailId, files).</summary>
    [HttpPost("mail/send"), RequirePerm("mail.view"), EnableRateLimiting("ai"), RequestSizeLimit(6_000_000), RequestFormLimits(MultipartBodyLengthLimit = 6_000_000)]
    public async Task<object> Send([FromForm] SendMailForm form, CancellationToken ct)
    {
        static List<string> Split(string? s) => (s ?? "").Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList();
        var to = Split(form.To);
        var cc = Split(form.Cc).Except(to).ToList();
        var files = form.Files ?? [];
        var subject = (form.Subject ?? "").Trim();
        var body = (form.Body ?? "").Trim();
        var badAddr = to.Concat(cc).Where(a => !Validator.IsEmail(a)).ToList();
        var badFiles = files.Where(f => Blocked.Contains(Path.GetExtension(f.FileName))).Select(f => f.FileName).ToList();

        VendorEmail? original = null;
        if (form.ReplyToEmailId is int rid)
            original = await db.VendorEmails.AsNoTracking().FirstOrDefaultAsync(e => e.VendorEmailId == rid && e.UserId == me.RequiredId, ct)
                       ?? throw AppException.NotFound("Email");

        string? inboxReplyId = null;
        if (original is null && form.ReplyToInboxItemId is int iid)
            inboxReplyId = await db.InboxItems.AsNoTracking().Where(i => i.InboxItemId == iid && i.UserId == me.RequiredId).Select(i => i.MessageId).FirstOrDefaultAsync(ct)
                           ?? throw AppException.NotFound("Email");

        new Validator()
            .Check(to.Count > 0, "to", "Add at least one recipient")
            .Check(badAddr.Count == 0, "to", $"Not a valid email address: {string.Join(", ", badAddr)}")
            .Check(to.Count + cc.Count <= 20, "to", "Up to 20 recipients")
            .Check(original != null || inboxReplyId != null || subject.Length > 0, "subject", "Add a subject")
            .Check(subject.Length <= 255, "subject", "Keep the subject under 255 characters")
            .Check(body.Length > 0, "body", "Write a message")
            .Check(body.Length <= 20000, "body", "Keep the message under 20,000 characters")
            .Check(files.Count <= 5, "files", "Up to 5 attachments")
            .Check(files.Sum(f => f.Length) <= MaxAttachmentBytes, "files", "Attachments must be under 3 MB in total")
            .Check(badFiles.Count == 0, "files", $"This file type can't be sent: {string.Join(", ", badFiles)}")
            .ThrowIfInvalid();

        var attachments = new List<OutgoingAttachment>();
        foreach (var f in files)
        {
            using var ms2 = new MemoryStream();
            await f.CopyToAsync(ms2, ct);
            attachments.Add(new OutgoingAttachment(Path.GetFileName(f.FileName), string.IsNullOrEmpty(f.ContentType) ? "application/octet-stream" : f.ContentType, ms2.ToArray()));
        }

        // A reply carries a subject too: used if Outlook can't answer in the thread and it goes out as a new email.
        var mailSubject = subject.Length > 0 ? subject : original != null ? "RE: " + original.Subject : subject;
        await sender.SendAsync(me.RequiredId, new OutgoingMail(to, cc, mailSubject, body, attachments, original?.MessageId ?? inboxReplyId), ct);
        if (form.ReplyToInboxItemId is int answered)   // answering an AI Inbox email closes it and its follow-up tasks
        {
            await db.InboxItems.Where(i => i.InboxItemId == answered && i.UserId == me.RequiredId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, "Done"), CancellationToken.None);
            await db.InboxTasks.Where(t => t.InboxItemId == answered && t.Kind == "FollowUp" && t.Status == "Open")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Done").SetProperty(t => t.ClosedUtc, DateTime.UtcNow), CancellationToken.None);
        }

        var vendors = new VendorMatcher(await db.Vendors.AsNoTracking().Where(v => v.IsActive).ToListAsync(ct));
        var tracked = to.Concat(cc).Any(a => vendors.Match(a) != null);
        if (form.TemplateId is int tid)
        {
            var meId = me.RequiredId;
            await db.EmailTemplates.Where(t => t.TemplateId == tid && (t.IsShared || t.OwnerUserId == meId))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UseCount, t => t.UseCount + 1).SetProperty(t => t.LastUsedUtc, DateTime.UtcNow), CancellationToken.None);
        }
        await audit.LogAsync("MAIL_SEND", $"{(original != null ? "Replied" : "Sent email")} to {string.Join(", ", to)}: \"{(original != null ? "RE: " + original.Subject : subject)}\"" +
            (attachments.Count > 0 ? $" with {attachments.Count} attachment(s)" : ""));
        return new { sent = true, tracked };
    }

    /// <summary>Vendor addresses seen in your view (plus addresses saved on the vendor), most recent first, for the To field.</summary>
    [HttpGet("mail/contacts"), RequirePerm("mail.view")]
    public async Task<List<VendorContact>> Contacts([FromQuery] int? vendorId)
    {
        var vendors = await db.Vendors.AsNoTracking().Where(v => v.IsActive && (vendorId == null || v.VendorId == vendorId)).ToListAsync();
        var names = vendors.ToDictionary(v => v.VendorId, v => v.Name);
        var matcher = new VendorMatcher(vendors);
        var since = DateTime.UtcNow.AddDays(-180);
        var rows = await VisibleEmails(null).Where(e => e.SentUtc >= since && (vendorId == null || e.VendorId == vendorId))
            .OrderByDescending(e => e.SentUtc).Take(3000)
            .Select(e => new { e.Direction, e.FromAddress, e.FromName, e.ToAddresses, e.SentUtc }).ToListAsync();
        var seen = new Dictionary<string, VendorContact>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var addrs = r.Direction == "In" ? [(r.FromAddress, r.FromName)] : r.ToAddresses.Split(',').Select(a => (a.Trim(), (string?)null));
            foreach (var (a, n) in addrs)
                if (a.Length > 0 && !seen.ContainsKey(a) && matcher.Match(a) is int vid && names.TryGetValue(vid, out var vn))
                    seen[a] = new VendorContact(a, n, vid, vn, r.SentUtc);
        }
        foreach (var v in vendors)
            foreach (var a in v.Domains.Where(d => d.Contains('@')))
                seen.TryAdd(a, new VendorContact(a, null, v.VendorId, v.Name, null));
        return seen.Values.Take(200).ToList();
    }

    // ---------- activity ----------

    private IQueryable<VendorEmail> VisibleEmails(string? scope)
    {
        var visible = data.VisibleUsers().Select(u => u.UserId);
        var q = db.VendorEmails.AsNoTracking().Where(e => visible.Contains(e.UserId));
        var meId = me.RequiredId;
        return (scope ?? "").ToLowerInvariant() switch
        {
            "mine" => q.Where(e => e.UserId == meId),
            "team" when me.DataScope != "OWN" => q.Where(e => db.UserHierarchy.Any(h => h.AncestorUserId == meId && h.DescendantUserId == e.UserId)),
            _ => q,
        };
    }

    /// <summary>Ids of outgoing emails that are the latest in their thread and older than the reply window.</summary>
    private static HashSet<int> Awaiting(IEnumerable<(int Id, int UserId, string? Conv, string Dir, DateTime Sent)> rows)
    {
        var cutoff = DateTime.UtcNow - ReplyWindow;
        return rows.GroupBy(r => (r.UserId, r.Conv ?? $"#{r.Id}"))
            .Select(g => g.OrderByDescending(r => r.Sent).First())
            .Where(r => r.Dir == "Out" && r.Sent < cutoff).Select(r => r.Id).ToHashSet();
    }

    /// <summary>Tracked vendor emails in your view with filters, newest first (max 500). AwaitingReply marks threads where
    /// your email has had no vendor answer for 48 hours.</summary>
    [HttpGet("mail/emails"), RequirePerm("mail.view")]
    public async Task<List<VendorEmailDto>> Emails([FromQuery] string? scope, [FromQuery] int? vendorId, [FromQuery] int? userId, [FromQuery] int? consultantId,
        [FromQuery] string? direction, [FromQuery] int days = 30, [FromQuery] bool awaitingOnly = false, [FromQuery] string? q = null)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
        var src = VisibleEmails(scope).Where(e => e.SentUtc >= since);
        if (vendorId is int v) src = src.Where(e => e.VendorId == v);
        if (userId is int u) src = src.Where(e => e.UserId == u);
        if (consultantId is int c) src = src.Where(e => e.ConsultantId == c);
        var rows = await src.OrderByDescending(e => e.SentUtc).Take(2000).Select(e => new
        {
            e.VendorEmailId, e.SentUtc, e.Direction, e.VendorId, Vendor = e.Vendor.Name, e.FromAddress, e.FromName, e.ToAddresses, e.Subject, e.Preview,
            e.Attachments, e.ConsultantId, e.UserId, UserName = e.User.FullName, e.ConversationId,
        }).ToListAsync();
        var awaiting = Awaiting(rows.Select(r => (r.VendorEmailId, r.UserId, r.ConversationId, r.Direction, r.SentUtc)));
        var cids = rows.Where(r => r.ConsultantId != null).Select(r => r.ConsultantId!.Value).Distinct().ToList();
        var cnames = cids.Count == 0 ? new Dictionary<int, string>()
            : await db.Consultants.IgnoreQueryFilters().Where(x => cids.Contains(x.ConsultantId)).ToDictionaryAsync(x => x.ConsultantId, x => x.FullName);

        IEnumerable<VendorEmailDto> list = rows.Select(r => new VendorEmailDto(r.VendorEmailId, r.SentUtc, r.Direction, r.VendorId, r.Vendor, r.FromAddress, r.FromName,
            r.ToAddresses, r.Subject, r.Preview, r.Attachments, r.ConsultantId, r.ConsultantId is int id ? cnames.GetValueOrDefault(id) : null,
            r.UserId, r.UserName, awaiting.Contains(r.VendorEmailId)));
        if (direction is "In" or "Out") list = list.Where(e => e.Direction == direction);
        if (awaitingOnly) list = list.Where(e => e.AwaitingReply);
        if (!string.IsNullOrWhiteSpace(q))
            list = list.Where(e => $"{e.Subject} {e.Preview} {e.FromAddress} {e.FromName} {e.ToAddresses} {e.Vendor} {e.ConsultantName}".Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
        return list.Take(500).ToList();
    }

    /// <summary>Sent/received totals, per-vendor and per-recruiter activity for the last <paramref name="days"/> days.</summary>
    [HttpGet("mail/summary"), RequirePerm("mail.view")]
    public async Task<MailSummary> Summary([FromQuery] string? scope, [FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);
        var rows = await VisibleEmails(scope).Where(e => e.SentUtc >= since)
            .Select(e => new { e.VendorEmailId, e.UserId, e.VendorId, e.ConversationId, e.Direction, e.SentUtc }).ToListAsync();
        var awaiting = Awaiting(rows.Select(r => (r.VendorEmailId, r.UserId, r.ConversationId, r.Direction, r.SentUtc)));
        var vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.Name).ToListAsync();
        var byVendor = vendors.Select(v =>
        {
            var x = rows.Where(r => r.VendorId == v.VendorId).ToList();
            return new VendorDto(v.VendorId, v.Name, v.Domains, v.IsActive, x.Count(r => r.Direction == "Out"), x.Count(r => r.Direction == "In"),
                x.Count > 0 ? x.Max(r => r.SentUtc) : null, x.Count(r => awaiting.Contains(r.VendorEmailId)));
        }).OrderByDescending(v => v.Sent + v.Received).ThenBy(v => v.Name).ToList();

        var users = await data.ScopeUsersAsync(scope == "mine" ? null : scope);
        if (me.DataScope == "OWN" || scope == "mine") users = users.Where(u => u.Id == me.RequiredId).ToList();
        var conns = await db.MailConnections.AsNoTracking().ToDictionaryAsync(c => c.UserId);
        var byRecruiter = users.Select(u =>
        {
            var x = rows.Where(r => r.UserId == u.Id).ToList();
            var c = conns.GetValueOrDefault(u.Id);
            return new RecruiterMailStat(u.Id, u.Name, x.Count(r => r.Direction == "Out"), x.Count(r => r.Direction == "In"), x.Select(r => r.VendorId).Distinct().Count(),
                x.Count(r => awaiting.Contains(r.VendorEmailId)), x.Count > 0 ? x.Max(r => r.SentUtc) : null, c?.Mailbox, c?.Status);
        }).Where(r => r.Sent + r.Received > 0 || r.Mailbox != null || users.Count <= 15)
          .OrderByDescending(r => r.Sent + r.Received).ThenBy(r => r.Name).ToList();

        return new MailSummary(days, rows.Count(r => r.Direction == "Out"), rows.Count(r => r.Direction == "In"), rows.Select(r => r.VendorId).Distinct().Count(),
            awaiting.Count, byVendor, byRecruiter);
    }

    // ---------- email signature ----------

    /// <summary>Your email signature (added at the bottom of emails you write here): the one you saved here, else your Outlook
    /// signature (read from your sent mail), else a default from your profile. <c>source</c> says which.</summary>
    [HttpGet("mail/signature"), RequirePerm("mail.view")]
    public async Task<MailSignatureDto> Signature([FromServices] MailSignatureService sigs, CancellationToken ct)
    {
        var s = await sigs.GetAsync(me.RequiredId, ct);
        return new MailSignatureDto(s.Signature, s.Source == "custom", s.Default, s.Source, s.Outlook, MailSignatureService.PreviewHtml(s.Rich));
    }

    /// <summary>Saves your signature. Empty goes back to your Outlook signature (or the default one).</summary>
    [HttpPut("mail/signature"), RequirePerm("mail.view")]
    public async Task<MailSignatureDto> SaveSignature(MailSignatureInput input, [FromServices] MailSignatureService sigs, CancellationToken ct)
    {
        var text = (input.Signature ?? "").Replace("\r\n", "\n").Trim();
        new Validator().Check(text.Length <= 2000, "signature", "Keep the signature under 2,000 characters").ThrowIfInvalid();
        var u = await db.Users.FirstAsync(x => x.UserId == me.RequiredId, ct);
        u.EmailSignature = text.Length == 0 ? null : text;
        await db.SaveChangesAsync(ct);
        return await Signature(sigs, ct);
    }

    /// <summary>Reads your Outlook signature again (after you changed it in Outlook).</summary>
    [HttpPost("mail/signature/refresh"), RequirePerm("mail.view"), EnableRateLimiting("ai")]
    public Task<MailSignatureDto> RefreshSignature([FromServices] MailSignatureService sigs, CancellationToken ct)
    {
        sigs.Forget(me.RequiredId);
        return Signature(sigs, ct);
    }

    // ---------- vendor directory ----------

    /// <summary>Vendor names used on requirements that aren't in the vendor list yet.</summary>
    [HttpGet("vendors/suggestions"), RequirePerm("vendors.manage")]
    public async Task<List<string>> Suggestions()
    {
        var known = (await db.Vendors.Select(v => v.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = await db.Requirements.Select(r => r.Vendor).Distinct().ToListAsync();
        return names.Select(n => n.Trim()).Where(n => n.Length > 1 && !known.Contains(n) && n is not ("Direct client" or "Confidential"))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
    }

    /// <summary>Scans connected Outlook mailboxes for outside companies that look like staffing vendors (requirements, rates, C2C, RTR,
    /// two-way threads) and aren't in the vendor list yet. Reads sender, recipients, subject and first lines only, and saves nothing.
    /// Admins and Delivery Managers scan every mailbox, a Team Lead their own and their team's, a Recruiter only their own.
    /// <paramref name="mine"/> limits anyone to their own mailbox. Adding the results to the vendor list needs vendors.manage.</summary>
    [HttpGet("vendors/discover"), RequirePerm("mail.view"), EnableRateLimiting("ai")]
    public async Task<DiscoveryResult> Discover([FromServices] VendorDiscovery discovery, [FromQuery] int days = 30, [FromQuery] bool mine = false, CancellationToken ct = default)
    {
        if (!ms.Options.Configured)
            throw AppException.Conflict("MICROSOFT_NOT_CONFIGURED", "Outlook isn't set up", "Ask your admin to set up the Microsoft app registration first.");
        List<int> users = mine || me.DataScope == "OWN" ? [me.RequiredId]
            : me.DataScope == "ALL" ? await db.MailConnections.Select(c => c.UserId).ToListAsync(ct)
            : await db.UserHierarchy.Where(h => h.AncestorUserId == me.RequiredId).Select(h => h.DescendantUserId).ToListAsync(ct);
        if (!users.Contains(me.RequiredId)) users.Add(me.RequiredId);
        if (!await db.MailConnections.AnyAsync(c => users.Contains(c.UserId) && c.Status == "Active", ct))
            throw AppException.Conflict("MAIL_NOT_CONNECTED", users.Count == 1 ? "Outlook isn't connected" : "No Outlook mailboxes connected",
                users.Count == 1 ? "Connect your Outlook mailbox first, then look for vendors in it."
                                 : "Connect Outlook first (or have your team sign in with Microsoft), then scan for vendors.");
        var r = await discovery.ScanAsync(users, days, ct);
        await audit.LogAsync("VENDOR_DISCOVER", $"Scanned {r.Mailboxes} mailbox(es), {r.MessagesRead} emails, last {r.Days} days: {r.Vendors.Count} possible vendors");
        return r;
    }

    /// <summary>Runs the automatic vendor scan now (it also runs by itself every few hours) and returns the vendors it added.</summary>
    [HttpPost("vendors/auto-add"), RequirePerm("vendors.manage"), EnableRateLimiting("ai")]
    public async Task<object> AutoAddNow([FromServices] VendorAutoAdd auto, CancellationToken ct)
    {
        if (!ms.Options.Configured)
            throw AppException.Conflict("MICROSOFT_NOT_CONFIGURED", "Outlook isn't set up", "Ask your admin to set up the Microsoft app registration first.");
        var added = await auto.RunAsync(ct);
        return new { added = added.Count, vendors = added };
    }

    /// <summary>Adds several vendors at once (e.g. picked from a mailbox scan). Each one is validated on its own, so one bad entry
    /// doesn't stop the rest; mailboxes are re-checked once at the end.</summary>
    [HttpPost("vendors/batch"), RequirePerm("vendors.manage")]
    public async Task<VendorBatchResult> CreateMany(VendorBatchInput input)
    {
        var items = input.Vendors ?? [];
        if (items.Count is 0 or > 100) throw AppException.Bad("VALIDATION", "Pick vendors to add", "Choose between 1 and 100 vendors.");
        var created = new List<VendorDto>();
        var failed = new List<VendorBatchError>();
        foreach (var item in items)
        {
            try
            {
                var (name, domains) = await ValidateAsync(item, null);
                var v = new Vendor { Name = name, Domains = domains, CreatedByUserId = me.RequiredId };
                db.Vendors.Add(v);
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e)) { db.Entry(v).State = EntityState.Detached; throw Duplicate(name); }
                created.Add(new VendorDto(v.VendorId, v.Name, v.Domains, true, 0, 0, null, 0));
            }
            catch (AppException ex)
            {
                var why = ex.Errors?.Values.SelectMany(x => x).FirstOrDefault() ?? ex.Message;
                failed.Add(new VendorBatchError(item.Name ?? "", string.Join(", ", item.Domains ?? []), why));
            }
        }
        if (created.Count > 0)
        {
            await audit.LogAsync("VENDOR_CREATE", $"Added {created.Count} vendor(s): {string.Join(", ", created.Select(c => c.Name).Take(20))}");
            await RescanAsync();
        }
        return new VendorBatchResult(created, failed);
    }

    /// <summary>Adds a vendor with its email domains/addresses, then re-checks connected mailboxes for past emails with it.</summary>
    [HttpPost("vendors"), RequirePerm("vendors.manage")]
    public async Task<VendorDto> Create(VendorInput input)
    {
        var (name, domains) = await ValidateAsync(input, null);
        var v = new Vendor { Name = name, Domains = domains, CreatedByUserId = me.RequiredId };
        db.Vendors.Add(v);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e)) { throw Duplicate(name); }
        await audit.LogAsync("VENDOR_CREATE", $"Added vendor {name} ({string.Join(", ", domains)})");
        await RescanAsync();
        return new VendorDto(v.VendorId, v.Name, v.Domains, true, 0, 0, null, 0);
    }

    /// <summary>Renames a vendor, changes its domains or turns tracking off/on. New domains trigger a re-check of mailboxes.</summary>
    [HttpPut("vendors/{id:int}"), RequirePerm("vendors.manage")]
    public async Task<VendorDto> Update(int id, VendorInput input)
    {
        var v = await db.Vendors.FirstOrDefaultAsync(x => x.VendorId == id) ?? throw AppException.NotFound("Vendor");
        var (name, domains) = await ValidateAsync(input, id);
        var added = domains.Except(v.Domains).Any() || (input.Active == true && !v.IsActive);
        v.Name = name; v.Domains = domains; v.IsActive = input.Active ?? v.IsActive;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e)) { throw Duplicate(name); }
        await audit.LogAsync("VENDOR_UPDATE", $"Updated vendor {name}: {(v.IsActive ? "" : "inactive, ")}{string.Join(", ", domains)}");
        if (added) await RescanAsync();
        return new VendorDto(v.VendorId, v.Name, v.Domains, v.IsActive, 0, 0, null, 0);
    }

    private static readonly Regex DomainRe = new(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.Compiled);

    private async Task<(string Name, List<string> Domains)> ValidateAsync(VendorInput input, int? id)
    {
        var name = Regex.Replace(input.Name ?? "", @"\s+", " ").Trim();
        var entries = (input.Domains ?? []).Select(d => d.Trim().ToLowerInvariant().TrimStart('@')).Where(d => d.Length > 0).Distinct().ToList();
        var bad = entries.Where(d => d.Contains('@') ? !Validator.IsEmail(d) : !DomainRe.IsMatch(d)).ToList();
        var free = entries.Where(d => !d.Contains('@') && VendorMatcher.FreeMail.Contains(d)).ToList();
        var own = ms.Options.CompanyDomains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Intersect(entries).ToList();
        var others = await db.Vendors.Where(x => x.VendorId != (id ?? 0)).Select(x => new { x.Name, x.Domains }).ToListAsync();
        var taken = entries.Select(d => (d, owner: others.FirstOrDefault(o => o.Domains.Contains(d))?.Name)).Where(t => t.owner != null).ToList();
        new Validator()
            .Check(name.Length is >= 2 and <= 200, "name", "Enter the vendor name")
            .Check(entries.Count > 0, "domains", "Add at least one email domain (e.g. teksystems.com) or address")
            .Check(entries.Count <= 20, "domains", "Up to 20 domains or addresses per vendor")
            .Check(bad.Count == 0, "domains", $"Not a valid domain or email: {string.Join(", ", bad)}")
            .Check(free.Count == 0, "domains", $"{string.Join(", ", free)} is personal email. Add the person's full address instead (e.g. name@{free.FirstOrDefault()}).")
            .Check(own.Count == 0, "domains", $"{string.Join(", ", own)} is your own company's domain")
            .Check(taken.Count == 0, "domains", string.Join("; ", taken.Select(t => $"{t.d} already belongs to {t.owner}")))
            .ThrowIfInvalid();
        return (name, entries);
    }

    private static AppException Duplicate(string name) => AppException.Conflict("DUPLICATE_VENDOR", "Vendor already exists", $"There's already a vendor called {name}.");

    /// <summary>New domains also apply to older mail: re-read the last FirstSyncDays of every mailbox (already tracked emails are skipped).</summary>
    private async Task RescanAsync()
    {
        await db.MailConnections.ExecuteUpdateAsync(s => s.SetProperty(c => c.SyncedThroughUtc, (DateTime?)null));
        sync.Kick();
    }
}
