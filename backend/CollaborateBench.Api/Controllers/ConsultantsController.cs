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
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(c.Name), "name", "Full name is required")
            .Check(string.IsNullOrWhiteSpace(c.Email) || Validator.IsEmail(c.Email), "email", "Enter a valid email address")
            .Check(string.IsNullOrWhiteSpace(c.Phone) || c.Phone.Count(char.IsDigit) >= 10, "phone", "Phone needs at least 10 digits")
            .Check(c.Rate is >= 1 and <= 500 || (c.Status == "Lead" && c.Rate == 0), "rate", "Enter a pay rate between $1 and $500/hr")
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

    /// <summary>Plain-text hotlist email of available consultants in your view, ready to paste and send to vendors.</summary>
    [HttpGet("hotlist"), RequirePerm("bench.view")]
    public async Task<object> Hotlist([FromQuery] string? scope)
    {
        var list = (await data.ConsultantsAsync(scope)).Where(c => c.Status is "Bench" or "Marketing").ToList();
        if (list.Count == 0) throw AppException.Bad("EMPTY_HOTLIST", "Hotlist is empty", "There are no available consultants in your view.");
        var sb = new StringBuilder($"Subject: Daily Hotlist – {DateTime.UtcNow:MMM d, yyyy} | {list.Count} Consultants Available (C2C/W2)\n\nHi Partner,\n\n" +
                                   "Please find our available consultants below. Reply with matching requirements and we'll share resumes with RTR.\n\n");
        var i = 1;
        foreach (var c in list)
            sb.Append($"{i++}. {c.Name} – {c.Title}\n   Exp: {c.Exp}+ yrs | Visa: {c.Visa} | Location: {c.Location}{(c.Relocate ? " (open to relocate)" : "")} | Rate: ${c.Rate:0}/hr C2C\n   Skills: {string.Join(", ", c.Skills)}\n\n");
        sb.Append($"Thanks & regards,\n{me.Name}\n{me.Role} | Collaborate-Bench Sales");
        await audit.LogAsync("HOTLIST", $"Generated hotlist with {list.Count} consultants");
        return new { text = sb.ToString(), count = list.Count };
    }
}
