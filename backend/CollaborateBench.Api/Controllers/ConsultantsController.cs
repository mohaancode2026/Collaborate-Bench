using System.Text;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>The bench: consultants owned by recruiters. Team filters in AppDbContext limit every query to the caller's data scope.</summary>
[ApiController, Route("api/consultants")]
public sealed class ConsultantsController(AppDbContext db, DataService data, ICurrentUser me, AuditService audit, IDataProtectionProvider dp) : ControllerBase
{
    /// <summary>Passport numbers are encrypted at rest with this purpose string (keys in the Data Protection key ring).</summary>
    private IDataProtector Passports => dp.CreateProtector("Consultants.Passport.v1");

    /// <summary>Consultants in your view. <paramref name="scope"/> narrows to mine/team; <paramref name="status"/> and <paramref name="q"/> filter.</summary>
    [HttpGet, RequirePerm("bench.view")]
    public async Task<List<ConsultantDto>> List([FromQuery] string? scope, [FromQuery] string? status, [FromQuery] string? q)
    {
        // Sourced leads (status "Lead") only show when asked for explicitly; the bench views never mix them in.
        var list = await data.ConsultantsAsync(scope, leadsOnly: string.Equals(status, "Lead", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(status)) list = list.Where(c => c.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(q))
            list = list.Where(c => $"{c.Name} {c.Title} {c.Location} {c.Visa} {c.OwnerName} {string.Join(' ', c.Skills)}".Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        return list;
    }

    /// <summary>One consultant (404 when it doesn't exist or is outside your team).</summary>
    [HttpGet("{id:int}"), RequirePerm("bench.view")]
    public Task<ConsultantDto> Get(int id) => data.ConsultantAsync(id);

    private static void Validate(ConsultantInput c)
    {
        // Email and phone are required for bench consultants (vendors and interviews need them); a sourced lead may not have them yet.
        // The pay rate is optional: 0 = not agreed yet (it's asked for when submitting).
        var lead = c.Status == "Lead";
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(c.Name), "name", "Full name is required")
            .Check(lead || !string.IsNullOrWhiteSpace(c.Email), "email", "Email is required")
            .Check(string.IsNullOrWhiteSpace(c.Email) || Validator.IsEmail(c.Email), "email", "Enter a valid email address")
            .Check(lead || !string.IsNullOrWhiteSpace(c.Phone), "phone", "Phone is required")
            .Check(string.IsNullOrWhiteSpace(c.Phone) || c.Phone.Count(char.IsDigit) >= 10, "phone", "Phone needs at least 10 digits")
            .Check(c.Rate == 0 || c.Rate is >= 1 and <= 500, "rate", "Pay rate must be between $1 and $500/hr (or leave it empty)")
            .Check(c.Exp is >= 0 and <= 60, "exp", "Experience must be 0–60 years")
            .Check((c.Skills?.Count(s => !string.IsNullOrWhiteSpace(s)) ?? 0) > 0, "skills", "Add at least one skill")
            .Check(string.IsNullOrEmpty(c.Visa) || Domain.Visas.Contains(c.Visa), "visa", "Choose a valid visa status")
            .Check(string.IsNullOrEmpty(c.Status) || Domain.ConsultantStatuses.Contains(c.Status), "status", "Choose a valid status")
            .Check((c.Summary?.Length ?? 0) <= 600, "summary", "Keep the summary under 600 characters")
            .Check(string.IsNullOrWhiteSpace(c.LinkedInUrl) || LinkedIn.ProfileUrl(c.LinkedInUrl) != null, "linkedInUrl", "Enter a LinkedIn profile link, e.g. https://www.linkedin.com/in/name")
            .Check(string.IsNullOrWhiteSpace(c.PassportNumber) || System.Text.RegularExpressions.Regex.IsMatch(c.PassportNumber.Replace(" ", ""), "^[A-Za-z0-9]{6,12}$"),
                   "passportNumber", "Passport number should be 6–12 letters and digits")
            .Check(c.VisaValidUntil is null || c.VisaValidUntil.Value.Year is >= 2000 and <= 2100, "visaValidUntil", "Enter a valid date")
            .Check(c.UsExp is null || (c.UsExp >= 0 && c.UsExp <= c.Exp), "usExp", "US experience can't be more than total experience")
            .Check((c.Availability?.Length ?? 0) <= 60, "availability", "Keep availability under 60 characters")
            .Check((c.PreferredLocations?.Length ?? 0) <= 200, "preferredLocations", "Keep preferred locations under 200 characters")
            .Check((c.Education?.Length ?? 0) <= 300, "education", "Keep education under 300 characters")
            .Check((c.Certifications?.Length ?? 0) <= 500, "certifications", "Keep certifications under 500 characters")
            .ThrowIfInvalid();
    }

    /// <summary>Copies the vendor-submission details; encrypts a new passport number, keeps the old one when none is given.</summary>
    private void ApplyDetails(Consultant e, ConsultantInput c)
    {
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        e.LinkedInUrl = LinkedIn.ProfileUrl(c.LinkedInUrl);
        e.VisaValidUntil = c.VisaValidUntil is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;   // a date, not a moment: no time-zone shift
        e.Availability = T(c.Availability);
        e.PreferredLocations = T(c.PreferredLocations);
        e.Education = T(c.Education);
        e.Certifications = T(c.Certifications);
        e.UsExperienceYears = c.UsExp is int us ? (byte)us : null;
        if (c.ClearPassport) { e.PassportProtected = null; e.PassportLast4 = null; }
        else if (T(c.PassportNumber)?.Replace(" ", "").ToUpperInvariant() is { } pp)
        {
            e.PassportProtected = Passports.Protect(pp);
            e.PassportLast4 = pp[^4..];
        }
    }

    /// <summary>409 when another consultant (in any team) already has this LinkedIn profile.</summary>
    private async Task EnsureLinkedInFreeAsync(string? url, int? exceptId)
    {
        var norm = LinkedIn.ProfileUrl(url);
        if (norm is null) return;
        var other = await db.Consultants.IgnoreQueryFilters().Where(x => x.LinkedInUrl == norm && x.ConsultantId != exceptId && x.Status != "Inactive")
            .Select(x => new { x.FullName, Owner = x.Owner.FullName }).FirstOrDefaultAsync();
        if (other != null)
            throw new AppException(409, "DUPLICATE_CONSULTANT", "LinkedIn profile already used", $"{other.FullName} (owner: {other.Owner}) already has this LinkedIn profile.")
            { Errors = new Dictionary<string, string[]> { ["linkedInUrl"] = ["Another consultant already has this LinkedIn profile"] } };
    }

    /// <summary>The full passport number, for filling in a vendor submission. Needs edit rights; every view is audited.</summary>
    [HttpGet("{id:int}/passport"), RequirePerm("bench.edit")]
    public async Task<object> Passport(int id)
    {
        var c = await db.Consultants.AsNoTracking().Where(x => x.ConsultantId == id && x.Status != "Inactive")
            .Select(x => new { x.FullName, x.PassportProtected }).FirstOrDefaultAsync() ?? throw AppException.NotFound("Consultant");
        if (c.PassportProtected is null) throw AppException.NotFound("Passport number");
        string number;
        try { number = Passports.Unprotect(c.PassportProtected); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw AppException.Conflict("PASSPORT_UNREADABLE", "Passport number can't be read",
                "The encryption keys changed since it was saved. Enter the passport number again.");
        }
        await audit.LogAsync("PASSPORT_VIEW", $"Viewed the passport number of {c.FullName}", "Consultant", id);
        return new { passportNumber = number };
    }

    private static void Apply(Consultant e, ConsultantInput c, int owner)
    {
        e.OwnerUserId = owner;
        e.FullName = c.Name!.Trim();
        e.Title = c.Title?.Trim();
        e.Email = string.IsNullOrWhiteSpace(c.Email) ? null : c.Email.Trim().ToLowerInvariant();
        e.Phone = c.Phone?.Trim();
        e.VisaStatus = string.IsNullOrEmpty(c.Visa) ? null : c.Visa;
        e.Location = c.Location?.Trim();
        e.OpenToRelocate = c.Relocate;
        e.PayRate = c.Rate;
        e.ExperienceYears = (byte)c.Exp;
        e.Status = string.IsNullOrEmpty(c.Status) ? "Bench" : c.Status;
        e.Skills = (c.Skills ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        e.HotlistSummary = c.Summary?.Trim();
        e.UpdatedUtc = DateTime.UtcNow;
    }

    private static AppException DuplicateEmail() => new(409, "DUPLICATE_CONSULTANT", "Consultant already exists",
        "A consultant with this email is already on the bench (possibly owned by another team). Ask your Team Lead to check before adding a second profile.")
    { Errors = new Dictionary<string, string[]> { ["email"] = ["This email is already on the bench"] } };

    /// <summary>Adds a consultant, optionally saving the parsed resume. Only Team Lead and above may assign another owner.</summary>
    [HttpPost, RequirePerm("bench.create")]
    public async Task<ConsultantDto> Create(ConsultantInput input)
    {
        Validate(input);
        var owner = input.OwnerId ?? me.RequiredId;
        if (owner != me.RequiredId)
        {
            me.Require("bench.reassign");
            await data.EnsureCanAssignOwnerAsync(owner);
        }

        await EnsureLinkedInFreeAsync(input.LinkedInUrl, null);
        var entity = new Consultant();
        Apply(entity, input, owner);
        ApplyDetails(entity, input);
        db.Consultants.Add(entity);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e)) { throw DuplicateEmail(); }

        if (!string.IsNullOrWhiteSpace(input.ResumeText))
        {
            db.Resumes.Add(new Resume
            {
                ConsultantId = entity.ConsultantId, FileName = input.ResumeFileName, ExtractedText = input.ResumeText,
                ParsedBy = input.ParsedBy == "claude" ? "claude" : "rules", UploadedByUserId = me.RequiredId,
            });
            await db.SaveChangesAsync();
        }
        await audit.LogAsync("CONSULTANT_CREATE", $"Added {entity.FullName} to the bench{(input.ResumeText != null ? " (AI-parsed resume)" : "")}", "Consultant", entity.ConsultantId);
        return await data.ConsultantAsync(entity.ConsultantId);
    }

    /// <summary>Updates a consultant. Uses optimistic concurrency (rowVersion): 409 if someone else changed it first.</summary>
    [HttpPut("{id:int}"), RequirePerm("bench.edit")]
    public async Task<ConsultantDto> Update(int id, ConsultantInput input)
    {
        Validate(input);
        // Row-level security: a consultant outside the user's team simply isn't found.
        var entity = await db.Consultants.FirstOrDefaultAsync(c => c.ConsultantId == id && c.Status != "Inactive") ?? throw AppException.NotFound("Consultant");
        var owner = input.OwnerId ?? entity.OwnerUserId;
        var reassigned = owner != entity.OwnerUserId;
        if (reassigned)
        {
            me.Require("bench.reassign");
            await data.EnsureCanAssignOwnerAsync(owner);
        }

        if (!string.IsNullOrEmpty(input.RowVersion))
        {
            try { db.Entry(entity).Property(c => c.RowVer).OriginalValue = Convert.FromBase64String(input.RowVersion); }
            catch (FormatException) { throw AppException.Bad("VALIDATION", "Invalid version", "Reload the page and try again."); }
        }
        await EnsureLinkedInFreeAsync(input.LinkedInUrl, id);
        Apply(entity, input, owner);
        ApplyDetails(entity, input);
        try { await db.SaveChangesAsync(); }   // DbUpdateConcurrencyException → 409 "Changed by someone else"
        catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e)) { throw DuplicateEmail(); }

        await audit.LogAsync("CONSULTANT_UPDATE", $"Updated {entity.FullName}{(reassigned ? " and reassigned the owner" : "")}", "Consultant", id);
        return await data.ConsultantAsync(id);
    }

    /// <summary>Removes a consultant from the bench (soft delete: status Inactive). With active submissions it returns 409
    /// unless <paramref name="force"/> is true, in which case those submissions are withdrawn in the same transaction.</summary>
    [HttpDelete("{id:int}"), RequirePerm("bench.delete")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        var c = await data.ConsultantAsync(id);
        if (c.ActiveSubmissions > 0 && !force)
            throw AppException.Conflict("HAS_ACTIVE_SUBMISSIONS", "Consultant has active submissions",
                $"{c.Name} has {c.ActiveSubmissions} active submission(s). Confirm to withdraw them and remove the consultant.");

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Submissions.Where(s => s.ConsultantId == id && s.Stage != "Billing" && s.Stage != "Withdrawn")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Stage, "Withdrawn").SetProperty(x => x.StageChangedUtc, DateTime.UtcNow));
            await db.Consultants.Where(x => x.ConsultantId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Inactive").SetProperty(x => x.UpdatedUtc, DateTime.UtcNow));
            await tx.CommitAsync();
        }
        await audit.LogAsync("CONSULTANT_REMOVE", $"Removed {c.Name} from the bench", "Consultant", id);
        return NoContent();
    }

    /// <summary>Hotlist email of every available consultant in the company (Bench or Marketing), plus the consultants to pick from.
    /// Sending the hotlist is the Manager's job (plus Admin and Delivery Manager), so it covers the whole bench even though
    /// a Manager's own view is limited to their team.</summary>
    [HttpGet("hotlist"), RequirePerm("hotlist.send")]
    public async Task<object> Hotlist(CancellationToken ct)
    {
        var list = await data.HotlistConsultantsAsync(ct);
        if (list.Count == 0) throw AppException.Bad("EMPTY_HOTLIST", "Hotlist is empty", "There are no available consultants on the bench right now.");
        var (subject, body) = HotlistText(list, "Partner", "Please find our available consultants below. Reply with matching requirements and we'll share resumes with RTR.");
        var ids = list.Select(c => c.Id).ToList();
        var withResume = (await db.Resumes.IgnoreQueryFilters().Where(r => ids.Contains(r.ConsultantId) && (r.ExtractedText != "" || r.BlobUri != null)).Select(r => r.ConsultantId)
                .Union(db.TailoredResumes.IgnoreQueryFilters().Where(t => ids.Contains(t.ConsultantId)).Select(t => t.ConsultantId)).ToListAsync(ct)).ToHashSet();
        await audit.LogAsync("HOTLIST", $"Generated hotlist with {list.Count} consultants");
        // text keeps the old copy/paste shape (subject line on top); subject + body feed the email window.
        return new
        {
            text = $"Subject: {subject}\n\n{body}", subject, body, count = list.Count,
            consultants = list.Select(c => new
            {
                c.Id, c.Name, c.Title, c.Exp, c.Visa, c.Location, c.Rate, c.Status, Skills = c.Skills.Take(6), c.OwnerName, c.OwnerTeam,
                HasResume = withResume.Contains(c.Id),
            }),
        };
    }

    /// <summary>People the hotlist can go to inside the company: active Team Leads first (they assign the consultants), then the rest.</summary>
    [HttpGet("hotlist/recipients"), RequirePerm("hotlist.send")]
    public async Task<object> HotlistRecipients(CancellationToken ct)
    {
        var meId = me.RequiredId;
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive && u.UserId != meId)
            .Select(u => new { u.UserId, u.FullName, u.Email, Role = u.Role.Name, Team = u.Team != null ? u.Team.Name : null }).ToListAsync(ct);
        return users.OrderBy(u => u.Role == "Team Lead" ? 0 : 1).ThenBy(u => u.FullName)
            .Select(u => new { id = u.UserId, name = u.FullName, email = u.Email, role = u.Role, team = u.Team });
    }

    /// <summary>
    /// Sends the hotlist of the chosen consultants from your Outlook to the chosen people (e.g. the Team Leads), with each consultant's
    /// latest resume attached. Outlook takes about 3 MB of attachments per email, so big sets go out as several emails
    /// ("part 2 of 3"); a single resume that's too big on its own is listed as "on request".
    /// </summary>
    [HttpPost("hotlist/send"), RequirePerm("hotlist.send"), Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai")]
    public async Task<HotlistSendResult> SendHotlist(HotlistSendInput input, [FromServices] MailSender sender, [FromServices] ResumeFiles files,
        [FromServices] IConfiguration cfg, CancellationToken ct)
    {
        var cids = (input.ConsultantIds ?? []).Distinct().ToList();
        var uids = (input.RecipientUserIds ?? []).Distinct().ToList();
        new Validator()
            .Check(cids.Count is > 0 and <= 30, "consultantIds", "Pick 1 to 30 consultants")
            .Check(uids.Count is > 0 and <= 20, "recipientUserIds", "Pick 1 to 20 people to send it to")
            .Check((input.Note?.Length ?? 0) <= 2000, "note", "Keep the note under 2,000 characters")
            .ThrowIfInvalid();
        var all = await data.HotlistConsultantsAsync(ct);
        var picked = all.Where(c => cids.Contains(c.Id)).ToList();
        if (picked.Count == 0) throw AppException.Bad("VALIDATION", "Nobody to send", "Those consultants aren't available on the bench any more. Reload and pick again.");
        var to = await db.Users.AsNoTracking().Where(u => uids.Contains(u.UserId) && u.IsActive).Select(u => u.Email).ToListAsync(ct);
        if (to.Count == 0) throw AppException.Bad("VALIDATION", "Nobody to send to", "Pick at least one active person.");

        // Latest resume of each consultant, packed into emails of at most ~2.8 MB (Graph's sendMail limit is 4 MB after base64).
        const long Budget = 2_800_000;
        var batches = new List<List<OutgoingAttachment>> { new() };
        var onRequest = new List<string>();
        var noResume = new List<string>();
        long used = 0;
        if (input.AttachResumes != false)
            foreach (var c in picked)
            {
                var f = await files.LatestAsync(c.Id, c.Name, c.Title, ct);
                if (f is null) { noResume.Add(c.Name); continue; }
                if (f.Bytes.Length > Budget) { onRequest.Add(c.Name); continue; }
                if (used + f.Bytes.Length > Budget && batches[^1].Count > 0) { batches.Add(new()); used = 0; }
                batches[^1].Add(new OutgoingAttachment(f.FileName, f.ContentType, f.Bytes));
                used += f.Bytes.Length;
            }

        var (subject, body) = HotlistText(picked, "Team", string.IsNullOrWhiteSpace(input.Note)
            ? "Here is today's hotlist. Please market these consultants to your vendors; their latest resumes are attached."
            : input.Note.Trim());
        var extra = new StringBuilder();
        if (noResume.Count > 0) extra.Append($"\n\nNo resume on file yet: {string.Join(", ", noResume)}.");
        if (onRequest.Count > 0) extra.Append($"\n\nResume too large to attach, available on request: {string.Join(", ", onRequest)}.");
        var signature = (await HttpContext.RequestServices.GetRequiredService<MailSignatureService>().GetAsync(me.RequiredId, ct)).Signature;
        var parts = batches.Count;
        for (var n = 0; n < parts; n++)
        {
            var sub = parts == 1 ? subject : $"{subject} (part {n + 1} of {parts})";
            var text = n == 0
                ? $"{body}{extra}{(parts > 1 ? $"\n\nThe resumes are split over {parts} emails because of their size." : "")}\n\n{signature}"
                : $"Hi Team,\n\nMore resumes for today's hotlist (part {n + 1} of {parts}): {string.Join(", ", batches[n].Select(a => a.Name))}.\n\n{signature}";
            await sender.SendAsync(me.RequiredId, new OutgoingMail(to, [], InboxTriage.Cut(sub, 255), text, batches[n], null), ct);
        }
        var attached = batches.Sum(b => b.Count);
        await audit.LogAsync("HOTLIST_SEND", $"Sent the hotlist ({picked.Count} consultants, {attached} resumes) to {string.Join(", ", to)}{(parts > 1 ? $" in {parts} emails" : "")}");
        return new HotlistSendResult(picked.Count, attached, parts, to, noResume, onRequest);
    }

    /// <summary>Subject and body of a hotlist email.</summary>
    private static (string Subject, string Body) HotlistText(List<ConsultantDto> list, string greeting, string intro)
    {
        var subject = $"Daily Hotlist – {DateTime.UtcNow:MMM d, yyyy} | {list.Count} Consultant{(list.Count == 1 ? "" : "s")} Available (C2C/W2)";
        var sb = new StringBuilder($"Hi {greeting},\n\n{intro}\n\n");
        var i = 1;
        foreach (var c in list)
            sb.Append($"{i++}. {c.Name} – {c.Title}\n   Exp: {c.Exp}+ yrs | Visa: {c.Visa} | Location: {c.Location}{(c.Relocate ? " (open to relocate)" : "")} | Rate: {(c.Rate > 0 ? $"${c.Rate:0}/hr C2C" : "on request")}\n   Skills: {string.Join(", ", c.Skills)}\n\n");
        return (subject, sb.ToString().TrimEnd());
    }
}
