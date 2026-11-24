using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Services;

/// <summary>"Autopilot" section of appsettings.</summary>
public sealed class AutopilotOptions
{
    /// <summary>Address consultants open the RTR link on. Must be reachable from the internet in production.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5173";
    /// <summary>Company name used in the RTR statement.</summary>
    public string CompanyName { get; set; } = "Collaborate-Bench Sales";
    public int RtrValidHours { get; set; } = 72;
    /// <summary>
    /// Mailbox that sends Autopilot email alerts (an Outlook-connected user's address, e.g. a shared "autopilot@" account).
    /// Empty = each recruiter's alerts are sent from their own Outlook to themselves.
    /// </summary>
    public string NotifyFromEmail { get; set; } = "";
}

/// <summary>
/// The bench sales Autopilot. Each run:
/// <list type="number">
/// <item><b>Discover</b>: match every Bench/Marketing consultant to open requirements and saved external jobs (strong, visa-compatible matches only).</item>
/// <item><b>Prepare</b>: tailor the resume (truth-checked), suggest a rate, find the vendor contact and draft the submission email.</item>
/// <item>A recruiter reviews and approves (<see cref="ApproveAsync"/>): the consultant gets an RTR link with the resume attached.</item>
/// <item><b>Submit</b>: once the consultant approves, email the vendor from the approver's Outlook and add the submission to the pipeline.</item>
/// <item><b>Follow up</b>: reply in the vendor thread when an automatic reminder falls due (capped per thread).</item>
/// <item><b>Read replies</b>: classify vendor replies (interview, rejected, needs info…) and link them to the submission for one-click action.</item>
/// </list>
/// Nothing goes to a vendor without a recruiter's approval and the consultant's RTR.
/// </summary>
public sealed partial class AutopilotService(AppDbContext db, ResumeTailor tailor, MailSender mail, ClaudeClient claude, AuditService audit,
    Microsoft.Extensions.Options.IOptions<AutopilotOptions> options, ILogger<AutopilotService> log)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] Available = ["Bench", "Marketing"];
    private static readonly string[] Inactive = ["Withdrawn", "Billing"];

    public async Task<AutopilotSettings> SettingsAsync(CancellationToken ct = default)
    {
        var s = await db.AutopilotSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null) { db.AutopilotSettings.Add(s = new AutopilotSettings()); await db.SaveChangesAsync(ct); }
        return s;
    }

    /// <summary>One full cycle. Each step is independent: a failure is logged and the next step still runs.</summary>
    public async Task<string> RunAsync(bool includeAutomaticSteps, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        var parts = new List<string>();
        async Task Step(string name, Func<Task<int>> work)
        {
            try { var n = await work(); if (n > 0) parts.Add($"{name} {n}"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Autopilot step {Step} failed", name); parts.Add($"{name} failed"); }
        }
        // Work people already approved always finishes, even when the Autopilot is switched off.
        await Step("submitted", () => SubmitApprovedAsync(ct));
        await Step("RTRs expired", () => ExpireRtrsAsync(ct));
        if (includeAutomaticSteps && s.Enabled)
        {
            await Step("found", () => DiscoverAsync(s, ct));
            await Step("prepared", () => PrepareAsync(s, ct));
            if (s.AutoFollowUp) await Step("followed up", () => FollowUpsAsync(s, ct));
            if (s.ClassifyReplies) await Step("replies read", () => ClassifyRepliesAsync(ct));
        }
        if (s.EmailNotifications) await Step("emailed", () => SendNotificationsAsync(ct));
        if (includeAutomaticSteps)
        {
            s.LastRunUtc = DateTime.UtcNow;
            s.LastRunSummary = parts.Count > 0 ? string.Join(", ", parts) : s.Enabled ? "Nothing new" : "Autopilot is off";
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return string.Join(", ", parts);
    }

    // ================= 1. Discover =================

    private async Task<int> DiscoverAsync(AutopilotSettings s, CancellationToken ct)
    {
        var consultants = await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(c => Available.Contains(c.Status)).ToListAsync(ct);
        if (consultants.Count == 0) return 0;
        var reqs = (await db.Requirements.AsNoTracking().Where(r => r.Status == "Open").ToListAsync(ct)).Select(ToDto).ToList();
        var since = DateTime.UtcNow.AddDays(-14);
        var external = s.IncludeExternalJobs
            ? await db.ExternalJobs.AsNoTracking().Where(j => !j.IsHidden && j.RequirementId == null && j.LastSeenUtc >= since).ToListAsync(ct)
            : [];
        var submitted = (await db.Submissions.IgnoreQueryFilters().AsNoTracking().Select(x => new { x.ConsultantId, x.RequirementId }).ToListAsync(ct))
            .Select(x => (x.ConsultantId, x.RequirementId)).ToHashSet();
        var known = await db.AutopilotItems.AsNoTracking().Select(i => new { i.ConsultantId, i.RequirementId, i.ExternalJobId, i.CreatedUtc }).ToListAsync(ct);
        var seen = known.Select(k => (k.ConsultantId, k.RequirementId, k.ExternalJobId)).ToHashSet();
        var today = DateTime.UtcNow.Date;

        var added = 0;
        foreach (var c in consultants)
        {
            var room = s.MaxNewPerConsultant - known.Count(k => k.ConsultantId == c.ConsultantId && k.CreatedUtc >= today);
            if (room <= 0) continue;
            var dto = ToDto(c);
            var options = reqs.Where(r => !submitted.Contains((c.ConsultantId, r.Id)) && !seen.Contains((c.ConsultantId, r.Id, null)))
                .Select(r => (Match: Matching.Score(dto, r), ReqId: (int?)r.Id, ExtId: (int?)null, Req: r))
                .Concat(external.Where(j => !seen.Contains((c.ConsultantId, null, j.ExternalJobId)))
                    .Select(j => { var r = JobText.AsRequirement(j); return (Match: Matching.Score(dto, r), ReqId: (int?)null, ExtId: (int?)j.ExternalJobId, Req: r); }))
                .Where(o => o.Match.Score >= s.MinScore && o.Match.VisaOk)
                .OrderByDescending(o => o.Match.Score).Take(room).ToList();
            foreach (var o in options)
            {
                db.AutopilotItems.Add(new AutopilotItem
                {
                    ConsultantId = c.ConsultantId, OwnerUserId = c.OwnerUserId, RequirementId = o.ReqId, ExternalJobId = o.ExtId,
                    JobTitle = Cut(o.Req.Title, 300)!, Client = Cut(o.Req.Client, 200), Vendor = Cut(o.Req.Vendor, 200), Score = o.Match.Score, Why = Cut(o.Match.Why, 1000),
                });
                added++;
            }
        }
        await db.SaveChangesAsync(ct);
        return added;
    }

    // ================= 2. Prepare =================

    private async Task<int> PrepareAsync(AutopilotSettings s, CancellationToken ct)
    {
        var items = await db.AutopilotItems.Where(i => i.Status == "Found" || i.Status == "NeedsResume")
            .OrderByDescending(i => i.Score).Take(Math.Clamp(s.MaxPreparePerRun, 1, 100)).ToListAsync(ct);
        var done = 0;
        foreach (var item in items)
        {
            try { if (await PrepareOneAsync(item, ct)) done++; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Autopilot could not prepare item {Item}", item.AutopilotItemId);
                Fail(item, "Couldn't prepare this submission automatically. Open the consultant and job to check them, then retry.");
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return done;
    }

    private async Task<bool> PrepareOneAsync(AutopilotItem item, CancellationToken ct)
    {
        var c = await db.Consultants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.ConsultantId == item.ConsultantId, ct);
        if (c is null || !Available.Contains(c.Status)) { Skip(item, "The consultant is no longer on the bench."); return false; }
        var source = await db.Resumes.AsNoTracking().Where(r => r.ConsultantId == c.ConsultantId && r.ExtractedText != "")
            .OrderByDescending(r => r.UploadedUtc).ThenByDescending(r => r.ResumeId).FirstOrDefaultAsync(ct);
        if (source is null)
        {
            // One alert per consultant per day, however many jobs are waiting on the resume.
            var text = $"Upload {c.FullName}'s resume so the Autopilot can prepare their submissions.";
            var since = DateTime.UtcNow.AddDays(-1);
            if (item.Status != "NeedsResume"
                && !db.AutopilotNotifications.Local.Any(n => n.UserId == item.OwnerUserId && n.Text == text)
                && !await db.AutopilotNotifications.AnyAsync(n => n.UserId == item.OwnerUserId && n.Text == text && n.CreatedUtc >= since, ct))
                Notify(item.OwnerUserId, "problem", text, "/autopilot?tab=attention", item.AutopilotItemId);
            item.Status = "NeedsResume"; item.Error = "Upload this consultant's resume in the AI Resume Builder; the Autopilot then prepares the submission.";
            item.UpdatedUtc = DateTime.UtcNow; return false;
        }

        // What we're submitting to.
        ResumeTailor.Target target; string? description; RequirementDto reqDto; decimal? postedRate = null;
        if (item.RequirementId is int rid)
        {
            var r = await db.Requirements.AsNoTracking().FirstOrDefaultAsync(x => x.RequirementId == rid, ct);
            if (r is null || r.Status != "Open") { Skip(item, "The requirement was closed."); return false; }
            reqDto = ToDto(r);
            target = new(r.Title, r.EndClient, r.Skills, r.Description, r.Location, r.WorkMode);
            description = r.Description;
            item.BillRate = r.BillRate; item.ContractType = r.ContractType;
        }
        else
        {
            var j = await db.ExternalJobs.AsNoTracking().FirstOrDefaultAsync(x => x.ExternalJobId == item.ExternalJobId, ct);
            if (j is null || j.IsHidden) { Skip(item, "The external job was removed."); return false; }
            reqDto = JobText.AsRequirement(j);
            target = new(j.Title, j.Company, j.Skills, j.Description, j.Location, reqDto.Mode);
            description = j.Description;
            // A job board's pay figure is what the employer pays, not the vendor's bill rate. Only use it when it leaves a margin.
            postedRate = j.MaxHourly;
            item.BillRate = j.MaxHourly is > 0 && (c.PayRate <= 0 || j.MaxHourly >= c.PayRate) ? j.MaxHourly : null;
            item.ContractType = reqDto.Type;
        }

        // 1) Tailored resume (truth-checked against the original).
        var owner = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == item.OwnerUserId, ct);
        var cDto = ToDto(c);
        var res = await tailor.TailorAsync(cDto, source.ExtractedText, target, hideContact: true, focus: null, ct);
        var name = $"{target.Title}{(string.IsNullOrWhiteSpace(target.Client) ? "" : $" – {target.Client}")} (Autopilot)";
        var version = new TailoredResume
        {
            ConsultantId = c.ConsultantId, RequirementId = item.RequirementId, SourceResumeId = source.ResumeId, Name = Cut(name, 200)!,
            TargetTitle = Cut(target.Title, 300)!, TargetClient = Cut(target.Client, 200), JobDescription = item.RequirementId is null ? description : null,
            DocJson = JsonSerializer.Serialize(res.Doc, WebJson), Changes = res.Changes, Gaps = res.Gaps, Warnings = res.Warnings, Matched = res.Matched,
            Engine = res.Engine, Notice = Cut(res.Notice, 500), HideContact = true, CreatedByUserId = item.OwnerUserId,
        };
        db.TailoredResumes.Add(version);
        await db.SaveChangesAsync(ct);
        item.TailoredResumeId = version.TailoredResumeId;

        // 2) Rates: pay from the profile; submit suggested between pay and bill when the bill rate is known and leaves a margin.
        item.PayRate = c.PayRate > 0 ? c.PayRate : null;
        item.SubmitRate = item.BillRate is > 0 && c.PayRate > 0 ? Matching.TrySuggestRate(c.PayRate, item.BillRate.Value) : null;
        string? rateProblem = null;
        if (c.PayRate > 0 && item.RequirementId is null && item.BillRate is null && postedRate is > 0 and decimal posted)
            rateProblem = Matching.NoMarginMessage(c.PayRate, posted, fromJobPosting: true);
        else if (c.PayRate > 0 && item.BillRate is > 0 && item.SubmitRate is null)
            rateProblem = Matching.NoMarginMessage(c.PayRate, item.BillRate.Value, fromJobPosting: item.RequirementId is null);

        // 3) Vendor contact: the last person at this vendor who emailed the team, or an address in the posting.
        string? contactName = null;
        if (item.RequirementId is not null && !string.IsNullOrWhiteSpace(item.Vendor))
        {
            var vendorId = await db.Vendors.AsNoTracking().Where(v => v.Name == item.Vendor && v.IsActive).Select(v => (int?)v.VendorId).FirstOrDefaultAsync(ct);
            if (vendorId is int vid)
            {
                var last = await db.VendorEmails.AsNoTracking().Where(e => e.VendorId == vid && e.Direction == "In")
                    .OrderByDescending(e => e.SentUtc).Select(e => new { e.FromAddress, e.FromName }).FirstOrDefaultAsync(ct);
                item.VendorEmail = last?.FromAddress; contactName = last?.FromName;
            }
        }
        item.VendorEmail ??= JobText.ContactEmails(description).FirstOrDefault();

        // 4) Submission email from the team's "Profile submission" template (or a built-in one).
        var tpl = await db.EmailTemplates.AsNoTracking().Where(t => t.IsShared && t.Category == "Submission")
            .OrderBy(t => t.Name == "Profile submission" ? 0 : 1).ThenBy(t => t.TemplateId).FirstOrDefaultAsync(ct);
        var values = MergeValues(cDto, reqDto, owner, contactName, item.Vendor);
        var rendered = MergeFields.Render(tpl?.Subject ?? "Submission: {{consultant.name}} for {{requirement.title}}",
            tpl?.Body ?? "Hi {{contact.firstName}},\n\nPlease find the profile of {{consultant.name}} for the {{requirement.title}} role.\n\nThanks,\n{{me.name}}", values);
        item.EmailSubject = Cut(rendered.Subject, 255);
        item.EmailBody = rendered.Body;

        item.Status = "Prepared";
        Notify(item.OwnerUserId, "approval", $"{c.FullName} → {item.JobTitle}{(string.IsNullOrWhiteSpace(item.Client) ? "" : $" ({item.Client})")} · {item.Score}% match" +
            (res.Warnings.Count > 0 ? $" · check {res.Warnings.Count} resume item(s)" : ""), "/autopilot?tab=approval", item.AutopilotItemId);
        var notes = new List<string>();
        if (rateProblem != null) notes.Add(rateProblem);
        if (res.Warnings.Count > 0) notes.Add($"Check {res.Warnings.Count} item(s) flagged in the tailored resume before approving.");
        item.Error = notes.Count > 0 ? string.Join(" ", notes) : null;
        item.UpdatedUtc = DateTime.UtcNow;
        return true;
    }

    // ================= 3. Recruiter approval → RTR request =================

    /// <summary>
    /// Saves the recruiter's final edits and emails the consultant an RTR link (with the tailored resume) from the approver's Outlook.
    /// Validates everything first so nothing is sent with a wrong rate or a missing address.
    /// </summary>
    public async Task ApproveAsync(AutopilotItem item, AutopilotApproveInput input, int approverUserId, string approverName, CancellationToken ct)
    {
        if (item.Status is not ("Prepared" or "RtrDeclined" or "RtrExpired"))
            throw AppException.Conflict("WRONG_STATUS", "Can't approve this one", $"This submission is {Label(item.Status)}.");
        var c = await db.Consultants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.ConsultantId == item.ConsultantId, ct) ?? throw AppException.NotFound("Consultant");
        var to = SplitEmails(input.VendorEmail);
        var external = item.RequirementId is null;
        var bill = external ? input.BillRate ?? item.BillRate : item.BillRate;
        var v = new Validator()
            .Check(!string.IsNullOrWhiteSpace(c.Email) && Validator.IsEmail(c.Email), "consultant", $"{c.FullName} has no valid email on file, so the RTR can't be sent. Add it on the bench first.")
            .Check(to.Count > 0 && to.All(Validator.IsEmail), "vendorEmail", "Enter the vendor contact's email")
            .Check(!string.IsNullOrWhiteSpace(input.EmailSubject) && input.EmailSubject.Length <= 255, "emailSubject", "Add a subject (up to 255 characters)")
            .Check(!string.IsNullOrWhiteSpace(input.EmailBody) && input.EmailBody.Length <= 20000, "emailBody", "Write the email to the vendor")
            .Check(input.PayRate is > 0, "payRate", "Enter the consultant's pay rate")
            .Check(input.SubmitRate is > 0, "submitRate", "Enter the submit rate")
            .Check(bill is >= 10 and <= 400, "billRate", "Enter the bill rate ($10–$400/hr). Job boards rarely show it; use the vendor's figure.")
            // Say plainly when no submit rate can work, instead of two errors that contradict each other.
            .Check(bill is null || input.PayRate is not > 0 || bill >= input.PayRate, "billRate",
                bill is decimal b && input.PayRate is decimal p ? Matching.NoMarginMessage(p, b, fromJobPosting: external) : "")
            // At the pay rate (no margin) is allowed; below it isn't.
            .Check(input.SubmitRate >= input.PayRate, "submitRate", "Can't be below the pay rate")
            .Check(bill is null || input.PayRate > bill || input.SubmitRate <= bill, "submitRate", $"Can't exceed the ${bill:0}/hr bill rate")
            .Check(!external || (input.ContractType != null && Domain.ContractTypes.Contains(input.ContractType)), "contractType", "Choose C2C, W2 or 1099");
        v.ThrowIfInvalid();
        if (item.TailoredResumeId is null) throw AppException.Conflict("NO_RESUME", "No tailored resume", "This submission has no tailored resume yet. Let the Autopilot prepare it first.");

        item.VendorEmail = string.Join(", ", to); item.EmailSubject = input.EmailSubject!.Trim(); item.EmailBody = input.EmailBody!.Trim();
        item.PayRate = input.PayRate; item.SubmitRate = input.SubmitRate; item.BillRate = bill;
        if (external) item.ContractType = input.ContractType;

        // One-time link: only its SHA-256 hash is stored.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var o = options.Value;
        var where = string.IsNullOrWhiteSpace(item.Client) ? "" : $" at {item.Client}";
        var through = string.IsNullOrWhiteSpace(item.Vendor) || item.Vendor == "Direct client" ? "" : $" through {item.Vendor}";
        var statement = $"I, {c.FullName}, authorize {o.CompanyName} to represent me and to submit my resume for the {item.JobTitle} position{where}{through}, " +
                        $"at a pay rate of ${item.PayRate:0.##}/hr ({item.ContractType ?? "C2C"}). To my knowledge, no one else has submitted me to this position.";
        var rtr = new RtrRequest
        {
            AutopilotItemId = item.AutopilotItemId, TokenHash = Hash(token), ConsultantEmail = c.Email!, Statement = statement,
            ExpiresUtc = DateTime.UtcNow.AddHours(Math.Clamp(o.RtrValidHours, 1, 336)),
        };
        // Older pending links for this item stop working.
        await db.RtrRequests.Where(r => r.AutopilotItemId == item.AutopilotItemId && r.Status == "Pending")
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "Expired"), ct);
        db.RtrRequests.Add(rtr);

        var link = $"{o.PublicUrl.TrimEnd('/')}/rtr/{token}";
        var first = c.FullName.Split(' ')[0];
        var body = $"Hi {first},\n\nI'd like to submit your profile for this role:\n\n" +
                   $"  Position: {item.JobTitle}{where}\n  Submitted through: {(through.Length > 0 ? item.Vendor : "the client directly")}\n" +
                   $"  Your rate: ${item.PayRate:0.##}/hr ({item.ContractType ?? "C2C"})\n\n" +
                   $"The resume I'll send is attached. Please review it and approve (or decline) your Right to Represent here:\n{link}\n\n" +
                   $"The link expires in {o.RtrValidHours} hours. If you've already been submitted to this position by someone else, please decline.\n\nThanks,\n{approverName}";
        var doc = await ResumeAttachmentAsync(item.TailoredResumeId.Value, c.FullName, ct);
        await mail.SendAsync(approverUserId, new OutgoingMail([c.Email!], [], $"Please approve your submission: {item.JobTitle}{where}", body, [doc], null), ct);

        item.Status = "RtrSent"; item.ApprovedByUserId = approverUserId; item.ApprovedUtc = DateTime.UtcNow; item.Error = null; item.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        await audit.LogAsync("AUTOPILOT_APPROVE", $"Approved {c.FullName} for {item.JobTitle}{where}; RTR link sent to the consultant", "Consultant", c.ConsultantId);
    }

    // ================= 4. Consultant RTR response (public link) =================

    public async Task<RtrRequest?> FindRtrAsync(string token, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(token) || token.Length > 100 ? null : await db.RtrRequests.FirstOrDefaultAsync(r => r.TokenHash == Hash(token), ct);

    public async Task RespondRtrAsync(RtrRequest rtr, bool approve, string? signedName, string? reason, string? ip, string? userAgent, CancellationToken ct)
    {
        if (rtr.Status != "Pending") throw AppException.Conflict("RTR_DONE", "Already answered", "This request was already answered or replaced by a newer one.");
        if (rtr.ExpiresUtc < DateTime.UtcNow) throw AppException.Conflict("RTR_EXPIRED", "This link has expired", "Ask your recruiter to send a new link.");
        var item = await db.AutopilotItems.FirstAsync(i => i.AutopilotItemId == rtr.AutopilotItemId, ct);
        if (item.Status != "RtrSent") throw AppException.Conflict("RTR_DONE", "No longer needed", "This submission was changed or cancelled by your recruiter.");
        var name = Regex.Replace(signedName ?? "", @"\s+", " ").Trim();
        if (approve) new Validator().Check(name.Length is >= 3 and <= 200, "signedName", "Type your full name to sign").ThrowIfInvalid();
        new Validator().Check((reason?.Length ?? 0) <= 500, "reason", "Keep the reason under 500 characters").ThrowIfInvalid();

        rtr.Status = approve ? "Approved" : "Declined";
        rtr.RespondedUtc = DateTime.UtcNow; rtr.SignedName = approve ? name : null; rtr.DeclineReason = approve ? null : reason?.Trim();
        rtr.RespondedIp = Cut(ip, 64); rtr.UserAgent = Cut(userAgent, 400);
        item.Status = approve ? "RtrApproved" : "RtrDeclined";
        item.Error = approve ? null : $"The consultant declined{(string.IsNullOrWhiteSpace(reason) ? "." : $": {reason!.Trim()}")}";
        item.UpdatedUtc = DateTime.UtcNow;
        if (!approve)
        {
            var who = await db.Consultants.IgnoreQueryFilters().Where(x => x.ConsultantId == item.ConsultantId).Select(x => x.FullName).FirstAsync(ct);
            Notify(item.ApprovedByUserId ?? item.OwnerUserId, "rtr", $"{who} declined the RTR for {item.JobTitle}{(string.IsNullOrWhiteSpace(reason) ? "." : $": \"{reason!.Trim()}\"")}",
                "/autopilot?tab=approval", item.AutopilotItemId);
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(approve ? "RTR_APPROVED" : "RTR_DECLINED",
            $"{(approve ? $"RTR e-signed by \"{name}\"" : "RTR declined by the consultant")} for {item.JobTitle} (from {ip ?? "unknown IP"})", "Consultant", item.ConsultantId, userId: item.ApprovedByUserId);
    }

    // ================= 5. Submit to the vendor =================

    private async Task<int> SubmitApprovedAsync(CancellationToken ct)
    {
        var items = await db.AutopilotItems.Where(i => i.Status == "RtrApproved").Take(20).ToListAsync(ct);
        var done = 0;
        foreach (var item in items)
        {
            try { await SubmitOneAsync(item, ct); done++; }
            catch (AppException ex) { Fail(item, ex.Message); NotifyFailure(item); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Autopilot could not submit item {Item}", item.AutopilotItemId);
                Fail(item, "The submission couldn't be sent. Check Outlook is connected, then retry.");
                NotifyFailure(item);
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return done;
    }

    private async Task SubmitOneAsync(AutopilotItem item, CancellationToken ct)
    {
        var approver = item.ApprovedByUserId ?? throw new AppException(409, "NOT_APPROVED", "Not approved", "No recruiter approved this submission.");
        var c = await db.Consultants.IgnoreQueryFilters().FirstAsync(x => x.ConsultantId == item.ConsultantId, ct);
        if (c.Status is "Placed" or "Inactive") throw AppException.Conflict("CONSULTANT_UNAVAILABLE", "Consultant not available", $"{c.FullName} is {c.Status.ToLowerInvariant()}.");
        var rtr = await db.RtrRequests.AsNoTracking().Where(r => r.AutopilotItemId == item.AutopilotItemId && r.Status == "Approved")
            .OrderByDescending(r => r.RespondedUtc).FirstOrDefaultAsync(ct) ?? throw AppException.Conflict("NO_RTR", "No RTR", "The consultant's RTR approval wasn't found.");

        // The requirement: internal, or the external job imported as one.
        Requirement req;
        if (item.RequirementId is int rid)
        {
            req = await db.Requirements.FirstAsync(r => r.RequirementId == rid, ct);
            if (req.Status != "Open") throw AppException.Conflict("REQUIREMENT_CLOSED", "Requirement closed", $"\"{req.Title}\" was closed before submission.");
        }
        else
        {
            var job = await db.ExternalJobs.FirstAsync(j => j.ExternalJobId == item.ExternalJobId, ct);
            req = job.RequirementId is int existing ? await db.Requirements.FirstAsync(r => r.RequirementId == existing, ct) : new Requirement
            {
                Title = job.Title, EndClient = job.Company, Vendor = "Direct client", Tier = "Direct", Location = string.IsNullOrWhiteSpace(job.Location) ? "Remote" : job.Location,
                WorkMode = JobText.Mode(job), ContractType = item.ContractType ?? "C2C", BillRate = item.BillRate ?? 0, Skills = job.Skills.Count > 0 ? job.Skills : c.Skills.Take(5).ToList(),
                AcceptedVisas = job.Visas, Description = $"{job.Description}\n\n---\nImported by the Autopilot from {job.Publisher ?? "a job board"}.\nApply: {job.ApplyUrl}",
                Source = "External", PostedByUserId = approver,
            };
            if (req.RequirementId == 0) { db.Requirements.Add(req); await db.SaveChangesAsync(ct); job.RequirementId = req.RequirementId; }
            item.RequirementId = req.RequirementId;
        }
        if (await db.Submissions.IgnoreQueryFilters().AnyAsync(s => s.ConsultantId == c.ConsultantId && s.RequirementId == req.RequirementId, ct))
            throw AppException.Conflict("DUPLICATE_SUBMISSION", "Already submitted", $"{c.FullName} was already submitted to \"{req.Title}\".");

        // Email first: if it can't be sent, nothing is recorded as submitted.
        var doc = await ResumeAttachmentAsync(item.TailoredResumeId!.Value, c.FullName, ct);
        await mail.SendAsync(approver, new OutgoingMail(SplitEmails(item.VendorEmail), [], item.EmailSubject ?? $"Submission: {c.FullName}", item.EmailBody ?? "", [doc], null), ct);

        var sub = new Submission
        {
            ConsultantId = c.ConsultantId, RequirementId = req.RequirementId, OwnerUserId = c.OwnerUserId, SubmitRate = item.SubmitRate ?? 0, PayRate = item.PayRate ?? 0,
            MatchScore = (byte)Math.Clamp(item.Score, 0, 100), RtrSigned = true,
            Notes = $"RTR e-signed by \"{rtr.SignedName}\" on {rtr.RespondedUtc:yyyy-MM-dd HH:mm} UTC. Sent to {item.VendorEmail} by the Autopilot.",
        };
        db.Submissions.Add(sub);
        await db.SaveChangesAsync(ct);
        db.SubmissionStageHistory.Add(new SubmissionStageHistory { SubmissionId = sub.SubmissionId, ToStage = "Submitted", ChangedByUserId = approver });
        if (c.Status == "Bench") c.Status = "Marketing";
        item.Status = "Submitted"; item.SubmissionId = sub.SubmissionId; item.SubmittedUtc = DateTime.UtcNow; item.Error = null; item.UpdatedUtc = DateTime.UtcNow;
        var done = $"Submitted {c.FullName} for {req.Title} ({req.EndClient}) to {item.VendorEmail}. RTR e-signed by {rtr.SignedName}.";
        Notify(approver, "rtr", done, "/autopilot?tab=submitted", item.AutopilotItemId);
        if (c.OwnerUserId != approver) Notify(c.OwnerUserId, "rtr", done, "/autopilot?tab=submitted", item.AutopilotItemId);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("SUB_CREATE", $"Autopilot submitted {c.FullName} to {req.EndClient} ({req.Title}) with e-signed RTR", "Submission", sub.SubmissionId, userId: approver);
    }

    private async Task<int> ExpireRtrsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expired = await db.RtrRequests.Where(r => r.Status == "Pending" && r.ExpiresUtc < now).ToListAsync(ct);
        foreach (var r in expired)
        {
            r.Status = "Expired";
            var item = await db.AutopilotItems.FirstOrDefaultAsync(i => i.AutopilotItemId == r.AutopilotItemId && i.Status == "RtrSent", ct);
            if (item != null)
            {
                item.Status = "RtrExpired"; item.Error = "The consultant didn't answer in time. Approve again to send a new link."; item.UpdatedUtc = now;
                Notify(item.ApprovedByUserId ?? item.OwnerUserId, "rtr", $"The RTR link for {item.JobTitle} expired without an answer. Approve again to send a new one.", "/autopilot?tab=approval", item.AutopilotItemId);
            }
        }
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    // ================= 6. Automatic follow-ups =================

    private async Task<int> FollowUpsAsync(AutopilotSettings s, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.FollowUpReminders.Where(r => r.Status == "Open" && r.Auto && r.DueUtc <= now).OrderBy(r => r.DueUtc).Take(20).ToListAsync(ct);
        var sent = 0;
        foreach (var r in due)
        {
            var email = await db.VendorEmails.AsNoTracking().Include(e => e.User).FirstOrDefaultAsync(e => e.VendorEmailId == r.VendorEmailId, ct);
            if (email is null || email.Direction != "Out") continue;
            // Stop after MaxAutoFollowUps unanswered follow-ups in this thread: a person takes over from there.
            var thread = await db.VendorEmails.AsNoTracking().Where(e => e.UserId == email.UserId && e.ConversationId == email.ConversationId && email.ConversationId != null)
                .Select(e => e.Direction).ToListAsync(ct);
            if (thread.Count(d => d == "Out") > s.MaxAutoFollowUps || thread.Contains("In")) continue;
            var consultant = email.ConsultantId is int cid ? await db.Consultants.IgnoreQueryFilters().Where(x => x.ConsultantId == cid).Select(x => x.FullName).FirstOrDefaultAsync(ct) : null;
            var contact = await db.VendorEmails.AsNoTracking().Where(e => e.FromAddress == email.ToAddresses && e.FromName != null).Select(e => e.FromName).FirstOrDefaultAsync(ct);
            var first = contact?.Split(' ', ',')[0] is { Length: > 1 } f ? char.ToUpperInvariant(f[0]) + f[1..] : "there";
            var body = $"Hi {first},\n\nFollowing up on {(consultant != null ? $"{consultant}'s profile" : "the profile")} I sent below. Is there any update or feedback from the client?\n\nHappy to set up a quick call if that helps.\n\nThanks,\n{email.User.FullName}";
            try
            {
                await mail.SendAsync(email.UserId, new OutgoingMail(SplitEmails(email.ToAddresses), [], "", body, [], email.MessageId), ct);
                r.Status = "Done"; r.ClosedUtc = now; r.Note = Cut($"{r.Note} Followed up automatically.".Trim(), 500);
                sent++;
                Notify(email.UserId, "followup", $"Followed up with {email.ToAddresses} on \"{email.Subject}\".", "/vendor-mail", null);
                await audit.LogAsync("AUTOPILOT_FOLLOWUP", $"Autopilot followed up on \"{email.Subject}\"", userId: email.UserId);
            }
            // Outlook not connected / no send permission / original deleted: leave the reminder for the recruiter.
            catch (AppException ex) { log.LogInformation("Autopilot follow-up skipped for reminder {Id}: {Reason}", r.ReminderId, ex.Message); }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return sent;
    }

    // ================= 7. Read vendor replies =================

    private static readonly (string Intent, Regex Re)[] IntentRules =
    [
        ("rejected", new(@"not (be )?moving forward|rejected|not selected|not a (good )?fit|went with (another|a different)|other candidates?|will not proceed", RegexOptions.IgnoreCase)),
        ("closed", new(@"position (is |has been )?(filled|closed|on hold|cancell?ed)|(req|requirement|role) (is )?(closed|on hold|filled)|no longer (open|available)", RegexOptions.IgnoreCase)),
        ("interview", new(@"interview|schedule|availability|available for a call|time ?slots?|zoom|teams (meeting|call)|screening call", RegexOptions.IgnoreCase)),
        ("needs_info", new(@"please (send|share|provide)|could you (send|share)|need (your|the|a|his|her)|visa copy|i-?94|dl copy|driver'?s licen[cs]e|references|linkedin|\bRTR\b|updated resume", RegexOptions.IgnoreCase)),
        ("rate", new(@"\brate\b|\$\s?\d+|budget|lower|negotiat", RegexOptions.IgnoreCase)),
    ];

    public async Task<int> ClassifyRepliesAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-14);
        var emails = await db.VendorEmails.Include(e => e.Vendor).Where(e => e.Direction == "In" && e.Intent == null && e.SentUtc >= since)
            .OrderByDescending(e => e.SentUtc).Take(30).ToListAsync(ct);
        foreach (var e in emails)
        {
            var text = $"{e.Subject}\n{e.Preview}";
            string intent = "other", summary = "";
            var j = await claude.JsonAsync("ReplyIntent",
                "You read short replies from IT staffing vendors about consultant submissions and classify what the vendor wants.",
                $$""""
                Classify this vendor reply. Intents: interview (wants to schedule an interview or asks availability), rejected (not moving forward),
                closed (position filled, closed or on hold), needs_info (asks for documents, details, RTR, visa copy, references), rate (rate question or negotiation),
                other (anything else). Return {"intent": "...", "summary": "what they want, max 20 words"}.
                FROM: {{e.Vendor.Name}}
                EMAIL: """{{text}}"""
                """", 300, ct);
            if (j?["intent"]?.ToString() is { } ai && IntentRules.Any(r => r.Intent == ai) || j?["intent"]?.ToString() == "other")
            {
                intent = j!["intent"]!.ToString();
                summary = j["summary"]?.ToString() ?? "";
            }
            else
            {
                intent = IntentRules.FirstOrDefault(r => r.Re.IsMatch(text)).Intent ?? "other";
                summary = e.Preview.Length > 140 ? e.Preview[..140] + "…" : e.Preview;
            }
            e.Intent = intent;
            e.IntentSummary = Cut(summary, 300);
            e.IntentHandled = intent == "other";
            if (intent != "other") Notify(e.UserId, "reply", $"{e.Vendor.Name}: {IntentLabel(intent)}. {e.IntentSummary}".Trim(), "/autopilot?tab=replies", null);
            // The submission it is about: this consultant's active submission with this vendor (or their latest active one).
            if (e.ConsultantId is int cid)
            {
                var subs = await db.Submissions.IgnoreQueryFilters().AsNoTracking().Where(s => s.ConsultantId == cid && !Inactive.Contains(s.Stage))
                    .OrderByDescending(s => s.CreatedUtc).Select(s => new { s.SubmissionId, s.Requirement.Vendor }).ToListAsync(ct);
                e.SubmissionId = (subs.FirstOrDefault(s => string.Equals(s.Vendor, e.Vendor.Name, StringComparison.OrdinalIgnoreCase)) ?? subs.FirstOrDefault())?.SubmissionId;
            }
        }
        await db.SaveChangesAsync(ct);
        return emails.Count;
    }

    // ================= 8. Email alerts to recruiters =================

    private static readonly (string Kind, string Heading)[] Sections =
    [
        ("approval", "READY FOR YOUR APPROVAL"), ("reply", "VENDOR REPLIES"), ("rtr", "RTR AND SUBMISSIONS"),
        ("problem", "NEEDS YOUR ATTENTION"), ("followup", "FOLLOW-UPS SENT FOR YOU"),
    ];

    /// <summary>Queues an event for the user's next email digest (saved with the caller's SaveChanges).</summary>
    private void Notify(int userId, string kind, string text, string link, int? itemId) =>
        db.AutopilotNotifications.Add(new AutopilotNotification { UserId = userId, Kind = kind, Text = Cut(text, 600)!, Link = link, AutopilotItemId = itemId });

    private void NotifyFailure(AutopilotItem item) =>
        Notify(item.ApprovedByUserId ?? item.OwnerUserId, "problem", $"Couldn't submit for {item.JobTitle}: {item.Error}", "/autopilot?tab=attention", item.AutopilotItemId);

    private static string IntentLabel(string intent) => intent switch
    {
        "interview" => "interview request", "rejected" => "not moving forward", "closed" => "position closed or on hold",
        "needs_info" => "needs more information", "rate" => "rate question", _ => "reply",
    };

    /// <summary>
    /// Sends each recipient one email with everything queued for them, grouped by kind, respecting their preferences.
    /// Sent from Autopilot:NotifyFromEmail if that mailbox is connected, otherwise from the recipient's own Outlook.
    /// Anything that can't be emailed is marked Skipped/Failed (it's still on the Autopilot page); old items are dropped.
    /// </summary>
    private async Task<int> SendNotificationsAsync(CancellationToken ct)
    {
        var stale = DateTime.UtcNow.AddDays(-2);
        await db.AutopilotNotifications.Where(n => n.Status == "Pending" && n.CreatedUtc < stale)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Status, "Skipped").SetProperty(n => n.Error, "Too old to send"), ct);
        var pending = await db.AutopilotNotifications.Where(n => n.Status == "Pending").OrderBy(n => n.NotificationId).Take(500).ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var o = options.Value;
        var from = string.IsNullOrWhiteSpace(o.NotifyFromEmail) ? null
            : await db.MailConnections.AsNoTracking().Where(m => m.Mailbox == o.NotifyFromEmail.Trim() && m.Status == "Active").Select(m => (int?)m.UserId).FirstOrDefaultAsync(ct);
        var url = o.PublicUrl.TrimEnd('/');
        var sentEmails = 0;
        foreach (var group in pending.GroupBy(n => n.UserId))
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == group.Key, ct);
            var prefs = await db.NotificationPrefs.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == group.Key, ct) ?? new NotificationPrefs { UserId = group.Key };
            bool Wanted(string kind) => prefs.Enabled && kind switch
            {
                "approval" => prefs.Approvals, "reply" => prefs.VendorReplies, "rtr" => prefs.RtrUpdates, "problem" => prefs.Problems, "followup" => prefs.FollowUps, _ => false,
            };
            var items = group.Where(n => Wanted(n.Kind)).ToList();
            foreach (var n in group.Except(items)) { n.Status = "Skipped"; n.Error = "Turned off in the user's email alerts"; }
            if (user is null || !user.IsActive || items.Count == 0 || !Validator.IsEmail(user.Email))
            {
                foreach (var n in items) { n.Status = "Skipped"; n.Error = "No active user with an email address"; }
                continue;
            }

            var counts = Sections.Select(sec => (sec.Kind, N: items.Count(n => n.Kind == sec.Kind))).Where(x => x.N > 0)
                .Select(x => x.Kind switch
                {
                    "approval" => $"{x.N} to approve", "reply" => $"{x.N} vendor repl{(x.N == 1 ? "y" : "ies")}", "rtr" => $"{x.N} RTR update{(x.N == 1 ? "" : "s")}",
                    "problem" => $"{x.N} need{(x.N == 1 ? "s" : "")} attention", _ => $"{x.N} follow-up{(x.N == 1 ? "" : "s")} sent",
                });
            var body = new StringBuilder($"Hi {user.FullName.Split(' ')[0]},\n\nHere's what the Autopilot has for you:\n");
            foreach (var (kind, heading) in Sections)
            {
                var list = items.Where(n => n.Kind == kind).ToList();
                if (list.Count == 0) continue;
                body.Append($"\n{heading}\n");
                foreach (var n in list) body.Append($"• {n.Text}\n");
                body.Append($"  Open: {url}{list[0].Link}\n");
            }
            body.Append($"\nOpen the Autopilot: {url}/autopilot\n\nYou get these emails because Autopilot email alerts are on. Change them in the Autopilot under Email alerts.");

            try
            {
                await mail.SendAsync(from ?? user.UserId, new OutgoingMail([user.Email], [], $"Autopilot: {string.Join(" · ", counts)}", body.ToString(), [], null), ct);
                foreach (var n in items) { n.Status = "Sent"; n.SentUtc = DateTime.UtcNow; }
                sentEmails++;
            }
            // Sender's Outlook not connected or not allowed to send: the alerts stay visible in the app.
            catch (AppException ex)
            {
                foreach (var n in items) { n.Status = "Failed"; n.Error = Cut(ex.Message, 300); }
                log.LogInformation("Autopilot email alert to user {User} not sent: {Reason}", user.UserId, ex.Message);
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return sentEmails;
    }

    // ================= helpers =================

    private async Task<OutgoingAttachment> ResumeAttachmentAsync(int tailoredResumeId, string consultantName, CancellationToken ct)
    {
        var json = await db.TailoredResumes.AsNoTracking().Where(t => t.TailoredResumeId == tailoredResumeId).Select(t => t.DocJson).FirstAsync(ct);
        var doc = JsonSerializer.Deserialize<ResumeDoc>(json, WebJson) ?? new ResumeDoc { Name = consultantName };
        var file = Regex.Replace($"{consultantName} - Resume", @"[^\w\s.-]", "").Trim();
        return new OutgoingAttachment($"{file}.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ResumeDocx.Build(doc));
    }

    private static Dictionary<string, string?> MergeValues(ConsultantDto c, RequirementDto r, User me, string? contactName, string? vendor)
    {
        var first = contactName?.Split(' ', ',')[0];
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["consultant.name"] = c.Name, ["consultant.firstName"] = c.Name.Split(' ')[0], ["consultant.title"] = c.Title,
            ["consultant.experience"] = c.Exp > 0 ? c.Exp.ToString() : null, ["consultant.skills"] = string.Join(", ", c.Skills.Take(6)),
            ["consultant.visa"] = c.Visa, ["consultant.location"] = c.Location, ["consultant.relocation"] = c.Relocate ? "Open to relocate" : "Not open to relocate",
            ["consultant.summary"] = c.Summary, ["requirement.title"] = r.Title, ["requirement.client"] = r.Client, ["requirement.location"] = r.Location,
            ["requirement.mode"] = r.Mode, ["requirement.type"] = r.Type, ["requirement.rate"] = r.Rate > 0 ? $"${r.Rate:0}/hr" : null,
            ["vendor.name"] = vendor, ["contact.name"] = contactName,
            ["contact.firstName"] = first is { Length: > 1 } ? char.ToUpperInvariant(first[0]) + first[1..] : "there",
            ["me.name"] = me.FullName, ["me.firstName"] = me.FullName.Split(' ')[0], ["me.email"] = me.Email, ["today"] = DateTime.UtcNow.ToString("MMM d, yyyy"),
        };
    }

    public static ConsultantDto ToDto(Consultant c) => new()
    {
        Id = c.ConsultantId, Name = c.FullName, Title = c.Title ?? "", Email = c.Email ?? "", Phone = c.Phone ?? "", Visa = c.VisaStatus ?? "",
        Location = c.Location ?? "", Relocate = c.OpenToRelocate, Rate = c.PayRate, Exp = c.ExperienceYears ?? 0, Status = c.Status,
        Skills = c.Skills, Summary = c.HotlistSummary ?? "", OwnerId = c.OwnerUserId,
    };

    private static RequirementDto ToDto(Requirement r) => new()
    {
        Id = r.RequirementId, Title = r.Title, Client = r.EndClient, Vendor = r.Vendor, Tier = r.Tier, Location = r.Location, Mode = r.WorkMode,
        Type = r.ContractType, Rate = r.BillRate, Skills = r.Skills, Visas = r.AcceptedVisas, Description = r.Description, Source = r.Source, Status = r.Status,
    };

    public static string Label(string status) => status switch
    {
        "Found" => "waiting to be prepared", "NeedsResume" => "waiting for a resume", "Prepared" => "waiting for approval", "RtrSent" => "waiting for the consultant's RTR",
        "RtrApproved" => "being submitted", "Submitted" => "already submitted", "RtrDeclined" => "declined by the consultant", "RtrExpired" => "expired",
        "Skipped" => "skipped", "Failed" => "failed", _ => status,
    };

    private static void Skip(AutopilotItem i, string why) { i.Status = "Skipped"; i.Error = why; i.UpdatedUtc = DateTime.UtcNow; }
    private static void Fail(AutopilotItem i, string why) { i.Status = "Failed"; i.Error = Cut(why, 500); i.UpdatedUtc = DateTime.UtcNow; }
    private static List<string> SplitEmails(string? s) => (s ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList();
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}

/// <summary>Runs the Autopilot on its schedule, and right away when kicked (e.g. a consultant just approved an RTR).</summary>
public sealed class AutopilotWorker(IServiceScopeFactory scopes, ILogger<AutopilotWorker> log) : BackgroundService
{
    private readonly Channel<bool> _kicks = Channel.CreateUnbounded<bool>();

    /// <param name="fullRun">True = all steps (Run now); false = only finish approved work (submit, expire).</param>
    public void Kick(bool fullRun = false) => _kicks.Writer.TryWrite(fullRun);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();
        while (!stop.IsCancellationRequested)
        {
            bool? kick = null;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                wait.CancelAfter(TimeSpan.FromMinutes(2));
                kick = await _kicks.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { /* periodic check */ }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<AutopilotService>();
                var s = await svc.SettingsAsync(stop);
                var scheduled = s.Enabled && (s.LastRunUtc is null || DateTime.UtcNow - s.LastRunUtc >= TimeSpan.FromMinutes(Math.Max(10, s.RunEveryMinutes)));
                var summary = await svc.RunAsync(includeAutomaticSteps: kick == true || scheduled, stop);
                if (summary.Length > 0) log.LogInformation("Autopilot: {Summary}", summary);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Autopilot run failed"); }
        }
    }
}
