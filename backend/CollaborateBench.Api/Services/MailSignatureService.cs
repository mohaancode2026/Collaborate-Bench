using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Services;

/// <summary>An image inside the Outlook signature (logo, badge), sent inline with the email.</summary>
public sealed record SignatureImage(string ContentId, string Name, string ContentType, byte[] Bytes);

/// <param name="Text">The signature as text (what the email window shows; image placeholders removed).</param>
/// <param name="Html">The signature as Outlook wrote it (formatting, links, images as cid: references), when it could be read.</param>
public sealed record OutlookSignature(string Text, string? Html, List<SignatureImage> Images);

/// <param name="Source">custom (saved in the app) | outlook (read from your Outlook sent mail) | default (built from your profile).</param>
/// <param name="Outlook">The signature found in your Outlook sent mail, when there is one (even if a custom one is in use).</param>
public sealed record MailSignatureInfo(string Signature, string Source, string Default, string? Outlook, OutlookSignature? Rich);

/// <summary>
/// The signature added to emails sent from the app. Mail sent through Microsoft Graph doesn't get Outlook's signature, and Graph
/// can't read the signature setting, so it's taken from what Outlook actually put on the user's own emails: the block of lines
/// that ends most of their recent sent emails (new text only, without the quoted thread). Its HTML (with the logo images) is taken
/// from the newest of those emails, so emails sent from the app look like Outlook's. A signature saved in the app wins; a default
/// built from the profile is the last resort. Cached for 6 hours per user.
/// </summary>
public sealed class MailSignatureService(AppDbContext db, MailSync sync, MicrosoftAuth auth, IHttpClientFactory http,
    IOptions<MicrosoftOptions> ms, IConfiguration cfg, IMemoryCache cache, ILogger<MailSignatureService> log)
{
    private const int MaxImageBytes = 600_000;   // all signature images together; they go with every email

    public async Task<MailSignatureInfo> GetAsync(int userId, CancellationToken ct = default)
    {
        var u = await db.Users.AsNoTracking().Include(x => x.Role).FirstAsync(x => x.UserId == userId, ct);
        var mailbox = await db.MailConnections.AsNoTracking().Where(c => c.UserId == userId).Select(c => c.Mailbox).FirstOrDefaultAsync(ct);
        string Default(string role) => string.Join("\n", new[] { "Thanks & Regards,", u.FullName, role, cfg["Autopilot:CompanyName"], mailbox ?? u.Email }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var fallback = Default(CollaborateBench.Api.Security.RoleNames.Label(u.Role.Name));
        // Emails the app sent end with its default signature, under the role's old name or its new title: never learn either.
        var outlook = await OutlookAsync(userId, [fallback, Default(u.Role.Name)], ct);
        if (!string.IsNullOrWhiteSpace(u.EmailSignature)) return new(u.EmailSignature!, "custom", fallback, outlook?.Text, null);
        return outlook is null ? new(fallback, "default", fallback, null, null) : new(outlook.Text, "outlook", fallback, outlook.Text, outlook);
    }

    /// <summary>The signature HTML with its images inlined as data: URLs, for a preview in the browser.</summary>
    public static string? PreviewHtml(OutlookSignature? s) => s?.Html is null ? null
        : s.Images.Aggregate(s.Html, (h, i) => h.Replace("cid:" + i.ContentId, $"data:{i.ContentType};base64,{Convert.ToBase64String(i.Bytes)}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Forget the cached Outlook signature (e.g. after the user changed it in Outlook and asks to refresh).</summary>
    public void Forget(int userId) => cache.Remove(Key(userId));
    private static string Key(int userId) => $"outlook-sig:v2:{userId}";

    private async Task<OutlookSignature?> OutlookAsync(int userId, IReadOnlyList<string> appDefaults, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(userId), out OutlookSignature? hit)) return hit;
        OutlookSignature? sig = null;
        try
        {
            var conn = await db.MailConnections.FirstOrDefaultAsync(c => c.UserId == userId && c.Status == "Active", ct);
            if (conn != null)
            {
                var token = await sync.AccessTokenAsync(db, auth, conn, ct);
                var graph = ms.Value.GraphUrl.TrimEnd('/') + "/v1.0/me";
                var list = await GetAsync(token, $"{graph}/mailFolders/sentitems/messages?$select=id,uniqueBody&$orderby=sentDateTime desc&$top=25", text: true, ct);
                var mails = ((list?["value"] as JsonArray) ?? []).OfType<JsonObject>()
                    .Select(m => (Id: m["id"]?.GetValue<string>(), Body: m["uniqueBody"]?["content"]?.GetValue<string>()))
                    .Where(m => m.Id != null && m.Body != null).ToList();
                var text = Detect(mails.Select(m => m.Body!), appDefaults);
                if (text != null)
                {
                    sig = new OutlookSignature(text, null, []);
                    // The newest email that ends with it carries the real (HTML) signature and its images.
                    var key = Norm(text);
                    var source = mails.FirstOrDefault(m => Norm(CleanText(m.Body!)).EndsWith(key, StringComparison.Ordinal));
                    if (source.Id != null)
                        try { sig = await RichAsync(token, graph, source.Id, text) ?? sig; }
                        catch (Exception e) when (e is not OperationCanceledException) { log.LogInformation(e, "Signature HTML for user {User} not read; using text", userId); }
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogInformation(e, "Outlook signature for user {User} not read", userId); }
        // Remember a miss for a shorter time, so a newly connected mailbox is picked up soon.
        cache.Set(Key(userId), sig, sig is null ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(6));
        return sig;
    }

    private async Task<OutlookSignature?> RichAsync(string token, string graph, string messageId, string text, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(messageId);
        var msg = await GetAsync(token, $"{graph}/messages/{id}?$select=uniqueBody", text: false, ct);
        var html = msg?["uniqueBody"]?["content"]?.GetValue<string>();
        var firstLine = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (html is null || firstLine is null) return null;
        var sigHtml = ExtractSignatureHtml(html, firstLine);
        if (sigHtml is null) return null;

        // The images the signature shows (cid: references), with their bytes.
        var images = new List<SignatureImage>();
        var cids = Regex.Matches(sigHtml, @"cid:([^""'\s>)]+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cids.Count > 0)
        {
            var atts = await GetAsync(token, $"{graph}/messages/{id}/attachments", text: false, ct);
            long total = 0;
            foreach (var a in ((atts?["value"] as JsonArray) ?? []).OfType<JsonObject>())
            {
                var cidv = a["contentId"]?.GetValue<string>()?.Trim('<', '>');
                var b64 = a["contentBytes"]?.GetValue<string>();
                if (cidv is null || b64 is null || !cids.Contains(cidv, StringComparer.OrdinalIgnoreCase)) continue;
                var bytes = Convert.FromBase64String(b64);
                if (total + bytes.Length > MaxImageBytes) continue;
                total += bytes.Length;
                images.Add(new SignatureImage(cidv, a["name"]?.GetValue<string>() ?? cidv, a["contentType"]?.GetValue<string>() ?? "image/png", bytes));
            }
            // An image that couldn't be loaded would show as a broken picture: drop its <img>.
            foreach (var missing in cids.Where(c => !images.Any(i => i.ContentId.Equals(c, StringComparison.OrdinalIgnoreCase))))
                sigHtml = Regex.Replace(sigHtml, $@"<img[^>]*cid:{Regex.Escape(missing)}[^>]*>", "", RegexOptions.IgnoreCase);
        }
        return new OutlookSignature(text, sigHtml, images);
    }

    private async Task<JsonNode?> GetAsync(string token, string url, bool text, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token);
        if (text) req.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
        using var res = await http.CreateClient("microsoft").SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) { log.LogInformation("Graph {Url} for the signature: {Status}", new Uri(url).AbsolutePath, (int)res.StatusCode); return null; }
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    // ---------- text ----------
    private static readonly Regex Placeholder = new(@"\[cid:[^\]]*\]|<https?://[^>\s]+>|\[(image|logo)[^\]]*\]", RegexOptions.IgnoreCase);
    /// <summary>Text Outlook gives for a signature, without the image placeholders ("[cid:image001.png@…]") and image links.</summary>
    public static string CleanText(string s) => Regex.Replace(Placeholder.Replace(s.Replace("\r\n", "\n"), ""), @"[ \t]+\n", "\n");
    private static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>The block of last lines most of the emails share (at least 2 emails, 2–15 lines). Emails written in this app
    /// (they end with the app's default signature) are skipped so the app doesn't learn its own signature.</summary>
    public static string? Detect(IEnumerable<string> bodies, params string[] appDefaults) => Detect(bodies, (IReadOnlyList<string>)appDefaults);

    public static string? Detect(IEnumerable<string> bodies, IReadOnlyList<string> appDefaults)
    {
        var defaultKeys = appDefaults.Select(Norm).Where(k => k.Length > 0).Distinct().ToList();
        var mails = bodies
            .Select(b => CleanText(b).Split('\n').Select(l => l.TrimEnd()).Where(l => !Regex.IsMatch(l.Trim(), "^_{5,}$")).ToList())
            .Select(lines => { while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1); return lines; })
            .Where(lines => lines.Count >= 2 && !defaultKeys.Any(Norm(string.Join(" ", lines)).Contains))
            .Take(25).ToList();
        if (mails.Count < 2) return null;

        // Candidate signatures: the shared tail of every pair of emails; keep the one most emails end with (then the longest).
        var counts = new Dictionary<string, (int Support, List<string> Lines)>();
        for (var a = 0; a < mails.Count; a++)
            for (var b = a + 1; b < mails.Count; b++)
            {
                var k = 0;
                while (k < mails[a].Count && k < mails[b].Count && Norm(mails[a][^(k + 1)]) == Norm(mails[b][^(k + 1)])) k++;
                var tail = mails[a].Skip(mails[a].Count - Math.Min(k, 15)).ToList();
                while (tail.Count > 0 && tail[0].Trim().Length == 0) tail.RemoveAt(0);
                if (tail.Count(l => l.Trim().Length > 0) < 2) continue;
                var key = Norm(string.Join("\n", tail));
                if (!counts.ContainsKey(key))
                    counts[key] = (mails.Count(m => m.Count >= tail.Count && Norm(string.Join("\n", m.Skip(m.Count - tail.Count))) == key), tail);
            }
        var best = counts.Values.Where(v => v.Support >= 2).OrderByDescending(v => v.Support).ThenByDescending(v => v.Lines.Count).FirstOrDefault();
        if (best.Lines is null) return null;
        var text = string.Join("\n", best.Lines.Select(l => l.Trim())).Trim();
        return text.Length is > 3 and <= 2000 ? Regex.Replace(text, @"\n{3,}", "\n\n") : null;
    }

    // ---------- HTML ----------
    /// <summary>
    /// The signature part of an email's HTML: from the block (p/div/table/tr) where its first line starts, to the end of the body.
    /// Found by matching the visible text, so Outlook's markup between the words doesn't matter.
    /// </summary>
    public static string? ExtractSignatureHtml(string html, string firstLine)
    {
        var bodyStart = Regex.Match(html, @"<body[^>]*>", RegexOptions.IgnoreCase) is { Success: true } bs ? bs.Index + bs.Length : 0;
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase) is var be and >= 0 ? be : html.Length;
        // Visible text (lowercase, single spaces) with each character's position in the HTML.
        var text = new StringBuilder();
        var map = new List<int>();
        void Add(char c, int pos)
        {
            c = char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c);
            if (c == ' ' && (text.Length == 0 || text[^1] == ' ')) return;
            text.Append(c); map.Add(pos);
        }
        for (var i = bodyStart; i < bodyEnd; i++)
        {
            var ch = html[i];
            if (ch == '<')
            {
                var close = html.IndexOf('>', i);
                if (close < 0) break;
                if (Regex.IsMatch(html.AsSpan(i, close - i + 1).ToString(), @"^</?(br|p|div|tr|li|table)\b", RegexOptions.IgnoreCase)) Add(' ', i);
                i = close;
            }
            else if (ch == '&')
            {
                var semi = html.IndexOf(';', i);
                if (semi > i && semi - i <= 10) { foreach (var d in WebUtility.HtmlDecode(html[i..(semi + 1)])) Add(d, i); i = semi; }
                else Add(ch, i);
            }
            else Add(ch, i);
        }
        var needle = Norm(firstLine);
        var at = text.ToString().LastIndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return null;
        var pos = map[at];
        // Start at the block that holds the first line.
        var start = new[] { "<p", "<div", "<table", "<tr" }
            .Select(t => html.LastIndexOf(t, pos, StringComparison.OrdinalIgnoreCase)).Where(x => x >= bodyStart).DefaultIfEmpty(pos).Max();
        var sig = html[start..bodyEnd].Trim();
        // Remove anything that has no place in an email body.
        sig = Regex.Replace(sig, @"<script[\s\S]*?</script>|<style[\s\S]*?</style>|\son\w+=""[^""]*""", "", RegexOptions.IgnoreCase);
        return sig.Length is > 0 and <= 60_000 ? sig : null;
    }
}
