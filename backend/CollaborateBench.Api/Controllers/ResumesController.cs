using System.Text.Json;
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

/// <summary>
/// AI resume builder: keep each consultant's original resume, and build versions tailored to a client's requirement.
/// Tailoring only rewords, reorders and emphasises what the original says; skills the job needs but the resume doesn't show
/// are listed as gaps. Every version is checked against the original, and unverified content is flagged.
/// </summary>
[ApiController, Route("api/resumes")]
public sealed class ResumesController(AppDbContext db, DataService data, ResumeTailor tailor, ICurrentUser me, AuditService audit, ResumeStore store, ResumeFiles files) : ControllerBase
{
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private const int MinResumeChars = 200, MaxResumeChars = 60_000;

    /// <summary>The consultant's latest original resume, or 204 when none has been uploaded yet.</summary>
    [HttpGet("source/{consultantId:int}"), RequirePerm("bench.view")]
    public async Task<ActionResult<ResumeSourceDto?>> Source(int consultantId)
    {
        await data.ConsultantAsync(consultantId);   // 404 when outside your team
        var r = await LatestSourceAsync(consultantId);
        return r is null ? NoContent() : r;
    }

    /// <summary>Saves a new original resume for the consultant (text extracted from PDF/DOCX in the browser). Older versions are kept.</summary>
    [HttpPost("source"), RequirePerm("bench.edit")]
    public async Task<ResumeSourceDto> UploadSource(ResumeSourceInput input)
    {
        var c = await data.ConsultantAsync(input.ConsultantId);
        var text = (input.Text ?? "").Replace("\r\n", "\n").Trim();
        new Validator()
            .Check(text.Length >= MinResumeChars, "text", "The resume text is too short. Upload the full resume (PDF, DOCX or TXT) or paste it.")
            .Check(text.Length <= MaxResumeChars, "text", $"The resume is longer than {MaxResumeChars:N0} characters. Remove attachments or appendices and try again.")
            .Check((input.FileName?.Length ?? 0) <= 255, "fileName", "The file name is too long")
            .ThrowIfInvalid();
        var resume = new Resume { ConsultantId = c.Id, FileName = input.FileName?.Trim(), ExtractedText = text, ParsedBy = "upload", UploadedByUserId = me.RequiredId };
        db.Resumes.Add(resume);
        await db.SaveChangesAsync();
        await audit.LogAsync("RESUME_UPLOAD", $"Uploaded the original resume for {c.Name}{(input.FileName is { Length: > 0 } f ? $" ({f})" : "")}", "Consultant", c.Id);
        return (await LatestSourceAsync(c.Id))!;
    }

    /// <summary>
    /// Stores the uploaded file itself (PDF, DOCX, DOC, RTF, TXT; up to 10 MB) with an original resume, so the exact file the
    /// consultant gave can be sent to vendors. Without resumeId, it goes with the consultant's newest original resume.
    /// </summary>
    [HttpPost("source/file"), RequirePerm("bench.edit"), RequestSizeLimit(ResumeStore.MaxBytes + 100_000)]
    public async Task<ResumeSourceDto> UploadSourceFile([FromForm] int consultantId, [FromForm] int? resumeId, IFormFile? file, CancellationToken ct)
    {
        var c = await data.ConsultantAsync(consultantId);
        new Validator()
            .Check(file is { Length: > 0 }, "file", "Choose the resume file")
            .Check(file is null || file.Length <= ResumeStore.MaxBytes, "file", "The file is larger than 10 MB")
            .Check(file is null || ResumeStore.Allowed(file.FileName), "file", "Use a PDF, Word (DOCX/DOC), RTF or text file")
            .ThrowIfInvalid();
        var resume = await db.Resumes.Where(r => r.ConsultantId == c.Id && (resumeId == null || r.ResumeId == resumeId))
                         .OrderByDescending(r => r.UploadedUtc).ThenByDescending(r => r.ResumeId).FirstOrDefaultAsync(ct)
                     ?? throw AppException.NotFound("Resume");
        using var ms = new MemoryStream();
        await file!.CopyToAsync(ms, ct);
        var old = resume.BlobUri;
        resume.BlobUri = await store.SaveOriginalAsync(c.Id, resume.ResumeId, file.FileName, ms.ToArray(), ct);
        resume.FileName = Path.GetFileName(file.FileName) is { Length: > 255 } n ? n[..255] : Path.GetFileName(file.FileName);
        await db.SaveChangesAsync(ct);
        if (old != null && old != resume.BlobUri) store.Delete(old);
        await audit.LogAsync("RESUME_FILE", $"Stored the resume file {resume.FileName} for {c.Name}", "Consultant", c.Id);
        return (await LatestSourceAsync(c.Id))!;
    }

    /// <summary>Downloads the stored original file (as uploaded). 404 when only text was saved.</summary>
    [HttpGet("source/{consultantId:int}/file"), RequirePerm("bench.view")]
    public async Task<IActionResult> SourceFile(int consultantId, CancellationToken ct)
    {
        var c = await data.ConsultantAsync(consultantId);
        var r = await db.Resumes.AsNoTracking().Where(x => x.ConsultantId == c.Id && x.BlobUri != null)
                    .OrderByDescending(x => x.UploadedUtc).ThenByDescending(x => x.ResumeId).FirstOrDefaultAsync(ct)
                ?? throw AppException.NotFound("Resume file");
        var bytes = await store.ReadAsync(r.BlobUri, ct) ?? throw AppException.NotFound("Resume file");
        await audit.LogAsync("RESUME_DOWNLOAD", $"Downloaded {c.Name}'s original resume file", "Consultant", c.Id);
        return File(bytes, ResumeStore.ContentType(r.BlobUri!), r.FileName ?? $"{c.Name} - Resume{Path.GetExtension(r.BlobUri)}");
    }

    /// <summary>Writes the version's Word file to the resume folder (after it's created or edited).</summary>
    private async Task SaveVersionFileAsync(TailoredResume t, ResumeDoc doc, CancellationToken ct)
    {
        t.FilePath = await store.SaveVersionAsync(t.ConsultantId, t.TailoredResumeId, ResumeDocx.Build(doc), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds a version of the consultant's resume tailored to a requirement (requirementId) or a pasted job description
    /// (jobTitle + jobDescription). Needs an original resume on file (409 NO_RESUME otherwise). Saved as a new version.
    /// </summary>
    [HttpPost("tailor"), RequirePerm("parser.use"), EnableRateLimiting("ai")]
    public async Task<TailoredResumeDto> Tailor(TailorInput input, CancellationToken ct)
    {
        var c = await data.ConsultantAsync(input.ConsultantId);
        var source = await LatestSourceAsync(c.Id)
                     ?? throw AppException.Conflict("NO_RESUME", "No resume on file", $"Upload {c.Name}'s current resume first. Tailoring only uses facts from it.");

        ResumeTailor.Target target;
        if (input.RequirementId is int rid)
        {
            var r = await data.RequirementAsync(rid, ct);
            target = new(r.Title, r.Client, r.Skills, r.Description, r.Location, r.Mode);
        }
        else
        {
            new Validator()
                .Check(!string.IsNullOrWhiteSpace(input.JobTitle), "jobTitle", "Enter the job title, or pick a requirement")
                .Check((input.JobDescription?.Trim().Length ?? 0) >= 50, "jobDescription", "Paste the job description (at least a few lines)")
                .Check((input.JobDescription?.Length ?? 0) <= 20_000, "jobDescription", "Keep the job description under 20,000 characters")
                .ThrowIfInvalid();
            target = new(input.JobTitle!.Trim(), input.Client?.Trim(), [], input.JobDescription!.Trim(), null, null);
        }
        new Validator().Check((input.Focus?.Length ?? 0) <= 500, "focus", "Keep the emphasis note under 500 characters").ThrowIfInvalid();

        var res = await tailor.TailorAsync(c, source.Text, target, input.HideContact, input.Focus?.Trim(), ct);
        var name = $"{target.Title}{(string.IsNullOrWhiteSpace(target.Client) ? "" : $" – {target.Client}")}";
        var entity = new TailoredResume
        {
            ConsultantId = c.Id, RequirementId = input.RequirementId, SourceResumeId = source.ResumeId,
            Name = name.Length > 200 ? name[..200] : name, TargetTitle = target.Title.Length > 300 ? target.Title[..300] : target.Title,
            TargetClient = target.Client is { Length: > 200 } tc ? tc[..200] : target.Client,
            JobDescription = input.RequirementId is null ? target.Description : null,
            DocJson = JsonSerializer.Serialize(res.Doc, WebJson), Changes = res.Changes, Gaps = res.Gaps, Warnings = res.Warnings, Matched = res.Matched,
            Engine = res.Engine, Notice = res.Notice is { Length: > 500 } n ? n[..500] : res.Notice, HideContact = input.HideContact, CreatedByUserId = me.RequiredId,
        };
        db.TailoredResumes.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);
        await SaveVersionFileAsync(entity, res.Doc, CancellationToken.None);
        await audit.LogAsync("RESUME_TAILOR", $"Tailored {c.Name}'s resume for {name} ({res.Engine}; {res.Gaps.Count} gaps, {res.Warnings.Count} warnings)", "Consultant", c.Id);
        return await DtoAsync(entity.TailoredResumeId);
    }

    /// <summary>Tailored versions for a consultant, newest first.</summary>
    [HttpGet, RequirePerm("bench.view")]
    public async Task<List<TailoredResumeDto>> List([FromQuery] int consultantId)
    {
        await data.ConsultantAsync(consultantId);
        var ids = await db.TailoredResumes.Where(t => t.ConsultantId == consultantId).OrderByDescending(t => t.UpdatedUtc).Select(t => t.TailoredResumeId).Take(50).ToListAsync();
        var list = new List<TailoredResumeDto>();
        foreach (var id in ids) list.Add(await DtoAsync(id));
        return list;
    }

    /// <summary>One tailored version.</summary>
    [HttpGet("{id:int}"), RequirePerm("bench.view")]
    public Task<TailoredResumeDto> Get(int id) => DtoAsync(id);

    /// <summary>Saves the recruiter's edits. The edited version is checked against the original again and the warnings refreshed (nothing is removed).</summary>
    [HttpPut("{id:int}"), RequirePerm("bench.edit")]
    public async Task<TailoredResumeDto> Update(int id, TailoredResumeUpdate input)
    {
        var t = await db.TailoredResumes.FirstOrDefaultAsync(x => x.TailoredResumeId == id) ?? throw AppException.NotFound("Resume version");
        var c = await data.ConsultantAsync(t.ConsultantId);
        var doc = input.Doc ?? throw AppException.Bad("VALIDATION", "Nothing to save", "Send the edited resume.");
        ResumeTailor.Clean(doc);
        var name = Regex.Replace(input.Name ?? t.Name, @"\s+", " ").Trim();
        var size = JsonSerializer.Serialize(doc, WebJson).Length;
        new Validator()
            .Check(name.Length is >= 2 and <= 200, "name", "Give the version a name (2–200 characters)")
            .Check(doc.Summary.Length <= 3000, "summary", "Keep the summary under 3,000 characters")
            .Check(size <= 200_000, "doc", "The resume is too long to save")
            .ThrowIfInvalid();
        doc.Name = c.Name;   // the name always comes from the profile
        if (t.HideContact) { doc.Email = null; doc.Phone = null; }
        var source = t.SourceResumeId is int sid ? await db.Resumes.Where(r => r.ResumeId == sid).Select(r => r.ExtractedText).FirstOrDefaultAsync() : null;
        t.Warnings = source is null ? [] : ResumeTailor.Verify(doc, source, c, strip: false);
        t.DocJson = JsonSerializer.Serialize(doc, WebJson);
        t.Name = name;
        t.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await SaveVersionFileAsync(t, doc, CancellationToken.None);   // every save rewrites the stored Word file
        return await DtoAsync(id);
    }

    /// <summary>Deletes a tailored version (the original resume is never touched).</summary>
    [HttpDelete("{id:int}"), RequirePerm("bench.edit")]
    public async Task<IActionResult> Delete(int id)
    {
        var t = await db.TailoredResumes.FirstOrDefaultAsync(x => x.TailoredResumeId == id) ?? throw AppException.NotFound("Resume version");
        var c = await data.ConsultantAsync(t.ConsultantId);
        db.TailoredResumes.Remove(t);
        await db.SaveChangesAsync();
        store.Delete(t.FilePath);
        await audit.LogAsync("RESUME_DELETE", $"Deleted resume version \"{t.Name}\" for {c.Name}", "Consultant", c.Id);
        return NoContent();
    }

    /// <summary>
    /// The resume to attach to a vendor submission: the consultant's latest resume, whichever was saved most recently:
    /// a Resume Builder version (its stored Word file) or an uploaded original (the file exactly as uploaded, or a Word file
    /// of its text when only text was saved). 409 NO_RESUME when nothing is on file. Recorded in the audit log.
    /// </summary>
    [HttpGet("submission/{consultantId:int}"), RequirePerm("bench.view")]
    public async Task<IActionResult> ForSubmission(int consultantId, CancellationToken ct)
    {
        var c = await data.ConsultantAsync(consultantId);   // 404 when outside your team
        var file = await files.LatestAsync(c.Id, c.Name, c.Title, ct)
                   ?? throw AppException.Conflict("NO_RESUME", "No resume on file", $"Upload {c.Name}'s resume (Resume Parser or Resume Builder) to attach it to submissions.");
        await audit.LogAsync("RESUME_DOWNLOAD", $"Attached {c.Name}'s latest resume ({file.Kind}) to a submission email", "Consultant", c.Id);
        return File(file.Bytes, file.ContentType, file.FileName);
    }

    /// <summary>Downloads the version as a Word document (.docx). Recorded in the audit log, since resumes leave the system this way.</summary>
    [HttpGet("{id:int}/docx"), RequirePerm("bench.view")]
    public async Task<IActionResult> Docx(int id)
    {
        var dto = await DtoAsync(id);
        var bytes = ResumeDocx.Build(dto.Doc);
        await audit.LogAsync("RESUME_DOWNLOAD", $"Downloaded {dto.ConsultantName}'s resume \"{dto.Name}\"", "Consultant", dto.ConsultantId);
        var file = Regex.Replace($"{dto.ConsultantName} - {dto.TargetTitle}", @"[^\w\s.-]", "").Trim();
        return File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{(file.Length > 120 ? file[..120] : file)}.docx");
    }

    // ---------- helpers ----------

    private async Task<ResumeSourceDto?> LatestSourceAsync(int consultantId) =>
        await db.Resumes.AsNoTracking().Where(r => r.ConsultantId == consultantId && r.ExtractedText != "")
            .OrderByDescending(r => r.UploadedUtc).ThenByDescending(r => r.ResumeId)
            .Select(r => new ResumeSourceDto(r.ResumeId, r.FileName, r.ExtractedText, r.UploadedUtc,
                db.Users.Where(u => u.UserId == r.UploadedByUserId).Select(u => u.FullName).FirstOrDefault()))
            .FirstOrDefaultAsync();

    /// <summary>Loads a version the caller may see (its consultant must be in their view).</summary>
    private async Task<TailoredResumeDto> DtoAsync(int id)
    {
        var t = await db.TailoredResumes.AsNoTracking().Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.TailoredResumeId == id)
                ?? throw AppException.NotFound("Resume version");
        var c = await data.ConsultantAsync(t.ConsultantId);
        var doc = JsonSerializer.Deserialize<ResumeDoc>(t.DocJson, WebJson) ?? new ResumeDoc();
        return new TailoredResumeDto(t.TailoredResumeId, c.Id, c.Name, t.RequirementId, t.Name, t.TargetTitle, t.TargetClient, doc, t.Changes, t.Gaps,
            t.Warnings, t.Matched, t.Engine, t.Notice, t.HideContact, t.CreatedBy?.FullName, t.CreatedUtc, t.UpdatedUtc);
    }
}
