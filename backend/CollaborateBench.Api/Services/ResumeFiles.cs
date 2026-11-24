using System.Text.Json;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Services;

/// <param name="Kind">What was picked, for the audit log: a Resume Builder version, the original file, or the original's text.</param>
public sealed record ResumeFile(byte[] Bytes, string ContentType, string FileName, string Kind);

/// <summary>
/// The consultant's latest resume as a file, whichever was saved most recently: a Resume Builder version (its stored Word file)
/// or an uploaded original (the file exactly as uploaded, or a Word file of its text when only text was saved).
/// Callers check access first: this looks past the team filter so the hotlist can attach any available consultant's resume.
/// </summary>
public sealed class ResumeFiles(AppDbContext db, ResumeStore store)
{
    public const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <returns>Null when nothing is on file.</returns>
    public async Task<ResumeFile?> LatestAsync(int consultantId, string name, string? title, CancellationToken ct)
    {
        var version = await db.TailoredResumes.IgnoreQueryFilters().Where(t => t.ConsultantId == consultantId)
            .OrderByDescending(t => t.UpdatedUtc).ThenByDescending(t => t.TailoredResumeId).FirstOrDefaultAsync(ct);
        var original = await db.Resumes.IgnoreQueryFilters().AsNoTracking().Where(r => r.ConsultantId == consultantId && (r.ExtractedText != "" || r.BlobUri != null))
            .OrderByDescending(r => r.UploadedUtc).ThenByDescending(r => r.ResumeId).FirstOrDefaultAsync(ct);
        if (version is null && original is null) return null;

        if (version != null && (original is null || version.UpdatedUtc >= original.UploadedUtc))
        {
            // Newest is a Resume Builder version. Older versions saved before files were kept get their file written now.
            var bytes = await store.ReadAsync(version.FilePath, ct) ?? [];
            if (bytes.Length == 0)
            {
                var doc = JsonSerializer.Deserialize<ResumeDoc>(version.DocJson, WebJson) ?? new ResumeDoc { Name = name };
                bytes = ResumeDocx.Build(doc);
                version.FilePath = await store.SaveVersionAsync(consultantId, version.TailoredResumeId, bytes, ct);
                await db.SaveChangesAsync(ct);
            }
            return new ResumeFile(bytes, DocxType, $"{Clean($"{name} - {version.TargetTitle}")}.docx", $"version \"{version.Name}\"");
        }
        var stored = await store.ReadAsync(original!.BlobUri, ct);
        if (stored != null)
            return new ResumeFile(stored, ResumeStore.ContentType(original.BlobUri!), $"{Clean($"{name} - Resume")}{Path.GetExtension(original.BlobUri)}", "original file");
        var built = ResumeDocx.Build(new ResumeDoc { Name = name, Headline = title ?? "", AdditionalText = original.ExtractedText, WholeTextOnly = true });
        return new ResumeFile(built, DocxType, $"{Clean($"{name} - Resume")}.docx", "original (from its text)");
    }

    public static string Clean(string name)
    {
        var s = Regex.Replace(name, @"[^\w\s.-]", "").Trim();
        return s.Length > 120 ? s[..120] : s;
    }
}
