using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>
/// A consultant's profile page: the marketing intake details, their documents (DL, EAD, passport copy…), and assignment:
/// Admin / Delivery Manager / Manager hand consultants to Team Leads (bench.assign); a Team Lead hands them to a recruiter (bench.reassign).
/// </summary>
[ApiController, Route("api/consultants")]
public sealed class ConsultantProfileController(AppDbContext db, DataService data, ICurrentUser me, AuditService audit, ResumeStore store) : ControllerBase
{
    public static readonly string[] DocumentKinds = ["Resume", "Passport", "DriversLicense", "StateId", "EAD", "I20", "Visa", "I797", "SSN", "Degree", "Other"];
    private static readonly string[] IdTypes = ["Driver's License", "State ID", "None"];

    // =====================================================================================================
    // Assignment
    // =====================================================================================================

    /// <summary>Who you can hand consultants to: Team Leads (and recruiters) for bench.assign; your own recruiters for a Team Lead.</summary>
    [HttpGet("assignees"), RequirePerm("bench.view")]
    public async Task<List<Assignee>> Assignees(CancellationToken ct)
    {
        var meId = me.RequiredId;
        IQueryable<User> q;
        if (me.Has("bench.assign")) q = db.Users.AsNoTracking().Where(u => u.IsActive && (u.Role.Name == "Team Lead" || u.Role.Name == "Recruiter"));
        else if (me.Has("bench.reassign")) q = data.VisibleUsers().Where(u => u.IsActive && (u.Role.Name == "Recruiter" || u.UserId == meId));
        else return [];
        var rows = await q.Select(u => new
        {
            u.UserId, u.FullName, Role = u.Role.Name, Team = u.Team != null ? u.Team.Name : null,
            Count = db.Consultants.IgnoreQueryFilters().Count(c => c.OwnerUserId == u.UserId && (c.Status == "Bench" || c.Status == "Marketing")),
        }).ToListAsync(ct);
        return rows.OrderBy(r => r.Role == "Team Lead" ? 0 : 1).ThenBy(r => r.Team).ThenBy(r => r.FullName)
            .Select(r => new Assignee(r.UserId, r.FullName, r.Role, r.Team, r.Count)).ToList();
    }

    /// <summary>Assigns one or more consultants to a person. bench.assign: to a Team Lead (or straight to a recruiter);
    /// a Team Lead (bench.reassign): to a recruiter in their team, or back to themselves.</summary>
    [HttpPost("assign"), RequirePerm("bench.view")]
    public async Task<AssignResult> Assign(AssignInput input, [FromServices] MailSender sender, [FromServices] ResumeFiles files,
        [FromServices] IConfiguration cfg, CancellationToken ct)
    {
        var ids = (input.ConsultantIds ?? []).Distinct().ToList();
        new Validator()
            .Check(ids.Count is > 0 and <= 200, "consultantIds", "Pick 1 to 200 consultants")
            .Check((input.Note?.Length ?? 0) <= 1000, "note", "Keep the note under 1,000 characters")
            .ThrowIfInvalid();
        var target = await db.Users.AsNoTracking().Where(u => u.UserId == input.OwnerUserId && u.IsActive)
            .Select(u => new { u.UserId, u.FullName, u.Email, Role = u.Role.Name }).FirstOrDefaultAsync(ct) ?? throw AppException.NotFound("User");

        if (me.Has("bench.assign"))
        {
            if (target.Role is not ("Team Lead" or "Recruiter"))
                throw AppException.Bad("VALIDATION", "Pick a Team Lead", "Consultants are assigned to a Team Lead (or a Bench Sales Executive).");
        }
        else
        {
            me.Require("bench.reassign");
            if (target.Role != "Recruiter" && target.UserId != me.RequiredId)
                throw AppException.Bad("VALIDATION", "Pick a Bench Sales Executive", "Assign consultants to a Bench Sales Executive in your team.");
            await data.EnsureCanAssignOwnerAsync(target.UserId, ct);
        }

        // The team filter applies: only consultants you can see can be assigned.
        var cons = await db.Consultants.Where(c => ids.Contains(c.ConsultantId) && c.Status != "Inactive").ToListAsync(ct);
        if (cons.Count == 0) throw AppException.NotFound("Consultant");
        foreach (var c in cons) { c.OwnerUserId = target.UserId; c.UpdatedUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("CONSULTANT_ASSIGN", $"Assigned {(cons.Count == 1 ? cons[0].FullName : $"{cons.Count} consultants")} to {target.FullName} ({RoleNames.Label(target.Role)})",
            "Consultant", cons.Count == 1 ? cons[0].ConsultantId : null);

        // Tell the new owner, from your Outlook. A mail problem never undoes the assignment.
        if (input.Notify == false || target.UserId == me.RequiredId) return new AssignResult(cons.Count, target.FullName, target.Role);
        try
        {
            var (emailed, attached) = await EmailAssigneeAsync(target.FullName, target.Email, target.Role, cons, input, sender, files, cfg, ct);
            return new AssignResult(cons.Count, target.FullName, target.Role, emailed, null, attached);
        }
        catch (AppException e)
        {
            return new AssignResult(cons.Count, target.FullName, target.Role, false, e.Message);
        }
    }

    private async Task<(bool, int)> EmailAssigneeAsync(string name, string email, string role, List<Consultant> cons, AssignInput input,
        MailSender sender, ResumeFiles files, IConfiguration cfg, CancellationToken ct)
    {
        var appUrl = (cfg["Autopilot:PublicUrl"] ?? cfg["Microsoft:AppUrl"] ?? "").TrimEnd('/');
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        var next = role == "Team Lead" ? "Please assign each of them to a Bench Sales Executive in your team." : "Please start marketing them and match them to open jobs.";
        var body = new System.Text.StringBuilder($"Hi {first},\n\n");
        body.AppendLine(string.IsNullOrWhiteSpace(input.Note)
            ? $"I've assigned {(cons.Count == 1 ? "a consultant" : $"{cons.Count} consultants")} to you. {next}"
            : input.Note.Trim());
        body.AppendLine();
        var i = 1;
        foreach (var c in cons)
        {
            body.AppendLine($"{i++}. {c.FullName}{(string.IsNullOrWhiteSpace(c.Title) ? "" : " – " + c.Title)}");
            body.AppendLine($"   {c.ExperienceYears ?? 0}+ yrs | Visa: {c.VisaStatus ?? "?"} | {c.Location} | ${c.PayRate:0}/hr | Skills: {string.Join(", ", c.Skills.Take(8))}");
            if (appUrl.Length > 0) body.AppendLine($"   Profile: {appUrl}/bench/{c.ConsultantId}");
        }

        // Latest resumes, up to ~2.8 MB in all (Outlook's limit for one email); the rest are on their profiles.
        const long Budget = 2_800_000;
        var attachments = new List<OutgoingAttachment>();
        var left = new List<string>();
        long used = 0;
        if (input.AttachResumes != false)
            foreach (var c in cons.Take(20))
            {
                var f = await files.LatestAsync(c.ConsultantId, c.FullName, c.Title, ct);
                if (f is null) continue;
                if (used + f.Bytes.Length > Budget) { left.Add(c.FullName); continue; }
                attachments.Add(new OutgoingAttachment(f.FileName, f.ContentType, f.Bytes));
                used += f.Bytes.Length;
            }
        if (attachments.Count > 0) body.AppendLine().AppendLine($"Latest resume{(attachments.Count == 1 ? " is" : "s are")} attached.");
        if (left.Count > 0) body.AppendLine($"Resumes too large to attach (open their profile): {string.Join(", ", left)}.");
        var signature = (await HttpContext.RequestServices.GetRequiredService<MailSignatureService>().GetAsync(me.RequiredId, ct)).Signature;
        body.AppendLine().Append(signature);

        var subject = cons.Count == 1 ? $"Consultant assigned to you: {cons[0].FullName}" : $"{cons.Count} consultants assigned to you";
        await sender.SendAsync(me.RequiredId, new OutgoingMail([email], [], InboxTriage.Cut(subject, 255), body.ToString(), attachments, null), ct);
        await audit.LogAsync("ASSIGN_EMAIL", $"Emailed {name} about {(cons.Count == 1 ? cons[0].FullName : $"{cons.Count} consultants")} ({attachments.Count} resume(s))");
        return (true, attachments.Count);
    }

    // =====================================================================================================
    // Marketing intake profile
    // =====================================================================================================

    [HttpGet("{id:int}/profile"), RequirePerm("bench.view")]
    public async Task<ConsultantProfileDto> Profile(int id, CancellationToken ct)
    {
        await data.ConsultantAsync(id, ct);   // 404 when outside your view
        var c = await db.Consultants.AsNoTracking().FirstAsync(x => x.ConsultantId == id, ct);
        var priv = me.Has("bench.edit");
        return new ConsultantProfileDto(c.ConsultantId, c.LegalName, c.MarketingTechnology, c.Keywords, c.GoogleVoice, c.PersonalPhone,
            priv ? c.DateOfBirth : null, priv ? c.SsnLast4 : null, c.IdType, c.Bachelors, c.Masters, c.LocationConstraints, c.CurrentProjectEnd,
            c.DoNotSubmit, c.PassportLast4 != null ? "••••" + c.PassportLast4 : null, priv);
    }

    [HttpPut("{id:int}/profile"), RequirePerm("bench.edit")]
    public async Task<ConsultantProfileDto> SaveProfile(int id, ConsultantProfileInput p, CancellationToken ct)
    {
        static string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
        var ssn = string.IsNullOrWhiteSpace(p.SsnLast4) ? null : new string(p.SsnLast4.Where(char.IsDigit).ToArray());
        var age = p.DateOfBirth is { } dob ? (DateTime.UtcNow - dob).TotalDays / 365.25 : (double?)null;
        new Validator()
            .Check(ssn is null || ssn.Length == 4, "ssnLast4", "Enter only the last 4 digits of the SSN")
            .Check(string.IsNullOrEmpty(p.IdType) || IdTypes.Contains(p.IdType), "idType", "Choose Driver's License, State ID or None")
            .Check(age is null or (>= 16 and <= 90), "dateOfBirth", "Check the date of birth")
            .Check(string.IsNullOrWhiteSpace(p.GoogleVoice) || p.GoogleVoice.Count(char.IsDigit) >= 10, "googleVoice", "Phone needs at least 10 digits")
            .Check(string.IsNullOrWhiteSpace(p.PersonalPhone) || p.PersonalPhone.Count(char.IsDigit) >= 10, "personalPhone", "Phone needs at least 10 digits")
            .ThrowIfInvalid();
        var c = await db.Consultants.FirstOrDefaultAsync(x => x.ConsultantId == id && x.Status != "Inactive", ct) ?? throw AppException.NotFound("Consultant");
        c.LegalName = T(p.LegalName, 200); c.MarketingTechnology = T(p.MarketingTechnology, 200); c.Keywords = T(p.Keywords, 500);
        c.GoogleVoice = T(p.GoogleVoice, 30); c.PersonalPhone = T(p.PersonalPhone, 30);
        c.DateOfBirth = p.DateOfBirth is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;
        c.SsnLast4 = ssn; c.IdType = T(p.IdType, 20); c.Bachelors = T(p.Bachelors, 300); c.Masters = T(p.Masters, 300);
        c.LocationConstraints = T(p.LocationConstraints, 200); c.CurrentProjectEnd = T(p.CurrentProjectEnd, 60); c.DoNotSubmit = T(p.DoNotSubmit, 500);
        c.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("CONSULTANT_PROFILE", $"Updated {c.FullName}'s marketing profile", "Consultant", id);
        return await Profile(id, ct);
    }

    // =====================================================================================================
    // Documents
    // =====================================================================================================

    [HttpGet("{id:int}/documents"), RequirePerm("bench.view")]
    public async Task<List<ConsultantDocumentDto>> Documents(int id, CancellationToken ct)
    {
        await data.ConsultantAsync(id, ct);
        return await db.ConsultantDocuments.AsNoTracking().Where(d => d.ConsultantId == id).OrderByDescending(d => d.UploadedUtc)
            .Select(d => new ConsultantDocumentDto(d.ConsultantDocumentId, d.Kind, d.FileName, d.ContentType, d.SizeBytes, d.Note, d.ExpiresOn, d.UploadedBy.FullName, d.UploadedUtc))
            .ToListAsync(ct);
    }

    /// <summary>Uploads a document (multipart: kind, file, note?, expiresOn?). PDF, Word or a photo/scan; up to 10 MB.</summary>
    [HttpPost("{id:int}/documents"), RequirePerm("bench.edit"), RequestSizeLimit(11_000_000)]
    public async Task<ConsultantDocumentDto> Upload(int id, [FromForm] string? kind, [FromForm] string? note, [FromForm] DateTime? expiresOn, IFormFile? file, CancellationToken ct)
    {
        var c = await data.ConsultantAsync(id, ct);
        new Validator()
            .Check(file is { Length: > 0 }, "file", "Choose a file")
            .Check(file is null || file.Length <= ResumeStore.MaxBytes, "file", "Files must be 10 MB or smaller")
            .Check(file is null || ResumeStore.DocumentAllowed(file.FileName), "file", "Upload a PDF, Word file or a photo (JPG, PNG, HEIC)")
            .Check(DocumentKinds.Contains(kind ?? ""), "kind", "Choose what this document is")
            .Check((note?.Length ?? 0) <= 300, "note", "Keep the note under 300 characters")
            .ThrowIfInvalid();
        using var ms = new MemoryStream();
        await file!.CopyToAsync(ms, ct);
        var name = Path.GetFileName(file.FileName);
        var doc = new ConsultantDocument
        {
            ConsultantId = c.Id, Kind = kind!, FileName = name.Length > 200 ? name[^200..] : name, ContentType = ResumeStore.DocumentContentType(name),
            SizeBytes = file.Length, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            ExpiresOn = expiresOn is { } e ? DateTime.SpecifyKind(e.Date, DateTimeKind.Utc) : null, UploadedByUserId = me.RequiredId,
        };
        db.ConsultantDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        try { doc.FilePath = await store.SaveDocumentAsync(c.Id, doc.ConsultantDocumentId, name, ms.ToArray(), ct); }
        catch { db.ConsultantDocuments.Remove(doc); await db.SaveChangesAsync(CancellationToken.None); throw; }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("DOCUMENT_UPLOAD", $"Uploaded {Label(doc.Kind)} for {c.Name} ({doc.FileName})", "Consultant", c.Id);
        return new ConsultantDocumentDto(doc.ConsultantDocumentId, doc.Kind, doc.FileName, doc.ContentType, doc.SizeBytes, doc.Note, doc.ExpiresOn, me.Name, doc.UploadedUtc);
    }

    /// <summary>Downloads a document. Recorded in the audit log: these are identity documents.</summary>
    [HttpGet("{id:int}/documents/{docId:int}"), RequirePerm("bench.view")]
    public async Task<IActionResult> Download(int id, int docId, CancellationToken ct)
    {
        var c = await data.ConsultantAsync(id, ct);
        var doc = await db.ConsultantDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.ConsultantDocumentId == docId && d.ConsultantId == id, ct) ?? throw AppException.NotFound("Document");
        var bytes = await store.ReadAsync(doc.FilePath, ct) ?? throw AppException.NotFound("Document file");
        await audit.LogAsync("DOCUMENT_DOWNLOAD", $"Downloaded {c.Name}'s {Label(doc.Kind)} ({doc.FileName})", "Consultant", id);
        return File(bytes, doc.ContentType, doc.FileName);
    }

    /// <summary>Deleting an identity document needs the delete permission (Admin, Delivery Manager, Manager); Team Leads and recruiters can upload but not delete.</summary>
    [HttpDelete("{id:int}/documents/{docId:int}"), RequirePerm("bench.delete")]
    public async Task<IActionResult> DeleteDocument(int id, int docId, CancellationToken ct)
    {
        var c = await data.ConsultantAsync(id, ct);
        var doc = await db.ConsultantDocuments.FirstOrDefaultAsync(d => d.ConsultantDocumentId == docId && d.ConsultantId == id, ct) ?? throw AppException.NotFound("Document");
        db.ConsultantDocuments.Remove(doc);
        await db.SaveChangesAsync(ct);
        store.Delete(doc.FilePath);
        await audit.LogAsync("DOCUMENT_DELETE", $"Deleted {c.Name}'s {Label(doc.Kind)} ({doc.FileName})", "Consultant", id);
        return NoContent();
    }

    private static string Label(string kind) => kind switch
    {
        "DriversLicense" => "driver's license", "StateId" => "state ID", "I20" => "I-20", "I797" => "I-797", "EAD" => "EAD", "SSN" => "SSN card",
        _ => kind.ToLowerInvariant(),
    };
}
