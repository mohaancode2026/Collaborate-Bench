using CollaborateBench.Api.Infrastructure;

namespace CollaborateBench.Api.Services;

/// <summary>
/// Keeps resume files on disk: the original file a recruiter uploaded (Resume Parser, Resume Builder) and a Word file of every
/// Resume Builder version, rewritten whenever the version is saved. The folder is Storage:ResumesPath (default App_Data/resumes
/// under the API). The database stores paths relative to that folder, so the folder can be moved or backed up as a whole.
/// </summary>
public sealed class ResumeStore(IConfiguration cfg, IWebHostEnvironment env, ILogger<ResumeStore> log)
{
    public const long MaxBytes = 10 * 1024 * 1024;
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".rtf"] = "application/rtf",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
    };

    public static bool Allowed(string fileName) => Types.ContainsKey(Path.GetExtension(fileName ?? ""));
    public static string ContentType(string path) => Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    private string Root
    {
        get
        {
            var configured = cfg["Storage:ResumesPath"];
            var root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(env.ContentRootPath, "App_Data", "resumes")
                : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
            return Path.GetFullPath(root);
        }
    }

    /// <summary>Saves the original upload as c{consultant}/original-{resumeId}{ext}. Returns the relative path.</summary>
    public Task<string> SaveOriginalAsync(int consultantId, int resumeId, string fileName, byte[] bytes, CancellationToken ct) =>
        WriteAsync($"c{consultantId}/original-{resumeId}{Path.GetExtension(fileName).ToLowerInvariant()}", bytes, ct);

    /// <summary>Saves (or overwrites) a Resume Builder version as c{consultant}/version-{id}.docx. Returns the relative path.</summary>
    public Task<string> SaveVersionAsync(int consultantId, int tailoredResumeId, byte[] docx, CancellationToken ct) =>
        WriteAsync($"c{consultantId}/version-{tailoredResumeId}.docx", docx, ct);

    // ---------- consultant documents (DL, EAD, passport copy, I-20, …) ----------
    private static readonly Dictionary<string, string> DocTypes = new(Types, StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".heic"] = "image/heic", [".webp"] = "image/webp",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff",
    };
    public static bool DocumentAllowed(string fileName) => DocTypes.ContainsKey(Path.GetExtension(fileName ?? ""));
    public static string DocumentContentType(string path) => DocTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    /// <summary>Saves a consultant document as c{consultant}/doc-{documentId}{ext}. Returns the relative path.</summary>
    public Task<string> SaveDocumentAsync(int consultantId, int documentId, string fileName, byte[] bytes, CancellationToken ct) =>
        WriteAsync($"c{consultantId}/doc-{documentId}{Path.GetExtension(fileName).ToLowerInvariant()}", bytes, ct);

    /// <summary>The bytes of a stored file, or null when it's missing (moved, deleted or never written).</summary>
    public async Task<byte[]?> ReadAsync(string? relative, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var full = Full(relative);
        if (!File.Exists(full)) { log.LogWarning("Resume file missing: {Path}", relative); return null; }
        return await File.ReadAllBytesAsync(full, ct);
    }

    public void Delete(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return;
        try { var full = Full(relative); if (File.Exists(full)) File.Delete(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Couldn't delete resume file {Path}", relative); }
    }

    private async Task<string> WriteAsync(string relative, byte[] bytes, CancellationToken ct)
    {
        var full = Full(relative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            // Write to a temp file first so a half-written file never replaces a good one.
            var tmp = full + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, full, overwrite: true);
            return relative;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Couldn't save resume file {Path} under {Root}", relative, Root);
            throw new AppException(500, "RESUME_STORAGE", "Couldn't save the resume file",
                "The server couldn't write to its resume folder. Ask your admin to check Storage:ResumesPath and its permissions.");
        }
    }

    /// <summary>Resolves a relative path inside the root; anything that would escape the folder is refused.</summary>
    private string Full(string relative)
    {
        var root = Root;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/').TrimStart('/')));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AppException(400, "BAD_PATH", "Invalid file path", "That resume file path isn't allowed.");
        return full;
    }
}
