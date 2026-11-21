using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Services;

/// <summary>"Microsoft" section of appsettings: an Entra ID (Azure AD) app registration for your company tenant.</summary>
public sealed class MicrosoftOptions
{
    /// <summary>Your company's tenant id (a GUID or contoso.onmicrosoft.com). Only accounts from this tenant can sign in.</summary>
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Sign-in host. Change only for national clouds (e.g. https://login.microsoftonline.us for GCC High).</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com";
    /// <summary>Microsoft Graph host. National clouds use e.g. https://graph.microsoft.us.</summary>
    public string GraphUrl { get; set; } = "https://graph.microsoft.com";
    /// <summary>Must exactly match a redirect URI on the app registration.</summary>
    public string RedirectUri { get; set; } = "http://localhost:5173/api/auth/microsoft/callback";
    /// <summary>Where the React app runs; the callback sends the browser back here.</summary>
    public string AppUrl { get; set; } = "http://localhost:5173";
    /// <summary>Your own email domains. Mail from these counts as "sent to the vendor". The mailbox's own domain is always included.</summary>
    public string[] CompanyDomains { get; set; } = [];
    public int MailSyncMinutes { get; set; } = 10;
    public int FirstSyncDays { get; set; } = 30;
    public int MaxMessagesPerSync { get; set; } = 500;
    public bool Configured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed record MsTokens(string AccessToken, string? RefreshToken, string? Scope = null, int ExpiresIn = 3600);
public sealed record MsSignInState(string Verifier, string Mode, int? UserId);

/// <summary>Authorization-code flow with PKCE against Entra ID, done on the server. The browser never sees Microsoft tokens.</summary>
public sealed class MicrosoftAuth(IHttpClientFactory http, IOptions<MicrosoftOptions> options, IMemoryCache cache, ILogger<MicrosoftAuth> log)
{
    public const string Scopes = "openid profile email offline_access User.Read Mail.Read Mail.Send";
    public string Graph => Options.GraphUrl.TrimEnd('/');
    public MicrosoftOptions Options => options.Value;
    private string Authority => $"{Options.Instance.TrimEnd('/')}/{Uri.EscapeDataString(Options.TenantId)}/oauth2/v2.0";

    private static string Random(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <param name="mode">"login" (sign in) or "connect" (link the mailbox of an already signed-in user).</param>
    public string StartUrl(string mode, int? userId, string? loginHint = null)
    {
        if (!Options.Configured)
            throw new AppException(503, "MICROSOFT_NOT_CONFIGURED", "Microsoft sign-in isn't set up",
                "Ask your admin to add the Microsoft app registration (Microsoft:TenantId, ClientId, ClientSecret) to the API settings.");
        var state = Random(32);
        var verifier = Random(48);
        cache.Set($"ms-state:{state}", new MsSignInState(verifier, mode, userId), TimeSpan.FromMinutes(10));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var q = new Dictionary<string, string?>
        {
            ["client_id"] = Options.ClientId, ["response_type"] = "code", ["redirect_uri"] = Options.RedirectUri,
            ["response_mode"] = "query", ["scope"] = Scopes, ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["prompt"] = "select_account",
            ["login_hint"] = loginHint,
        };
        return $"{Authority}/authorize?" + string.Join("&", q.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
    }

    /// <summary>One-time use: the state is removed as soon as it's read, so a callback URL can't be replayed.</summary>
    public MsSignInState? TakeState(string? state)
    {
        if (string.IsNullOrEmpty(state) || !cache.TryGetValue($"ms-state:{state}", out MsSignInState? s)) return null;
        cache.Remove($"ms-state:{state}");
        return s;
    }

    public Task<MsTokens> RedeemCodeAsync(string code, string verifier, CancellationToken ct) => TokenAsync(new()
    {
        ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Options.RedirectUri, ["code_verifier"] = verifier,
    }, ct);

    public Task<MsTokens> RefreshAsync(string refreshToken, CancellationToken ct) => TokenAsync(new()
    {
        ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken,
    }, ct);

    private async Task<MsTokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = Options.ClientId;
        form["client_secret"] = Options.ClientSecret;
        form["scope"] = Scopes;
        using var res = await http.CreateClient("microsoft").PostAsync($"{Authority}/token", new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        JsonNode? j = null;
        // A non-JSON body (e.g. an HTML error page from a proxy) is handled below as a failed request; nothing to parse.
        try { j = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { }
        if (!res.IsSuccessStatusCode || j?["access_token"] is null)
        {
            var err = j?["error"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}";
            log.LogWarning("Microsoft token request ({Grant}) failed: {Error} {Description}", form["grant_type"], err, j?["error_description"]?.GetValue<string>()?.Split('\n')[0]);
            throw new AppException(err == "invalid_grant" ? 401 : 502, err == "invalid_grant" ? "MAIL_RECONNECT" : "MICROSOFT_ERROR",
                err == "invalid_grant" ? "Outlook needs to be reconnected" : "Microsoft sign-in failed",
                err == "invalid_grant" ? "Your Outlook connection expired or was revoked. Connect Outlook again." : "Microsoft didn't accept the sign-in. Try again, or ask your admin to check the app registration.");
        }
        var expires = j["expires_in"] is JsonValue ev && (ev.TryGetValue<int>(out var ei) || (ev.TryGetValue<string>(out var es) && int.TryParse(es, out ei))) ? ei : 3600;
        return new MsTokens(j["access_token"]!.GetValue<string>(), j["refresh_token"]?.GetValue<string>(), j["scope"]?.GetValue<string>(), expires);
    }

    /// <summary>The signed-in Microsoft account's email address and name.</summary>
    public async Task<(string Email, string Name)> MeAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Graph}/v1.0/me?$select=mail,userPrincipalName,displayName");
        req.Headers.Authorization = new("Bearer", accessToken);
        using var res = await http.CreateClient("microsoft").SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new AppException(502, "MICROSOFT_ERROR", "Microsoft sign-in failed", "Couldn't read your Microsoft profile. Try again.");
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var email = j?["mail"]?.GetValue<string>() ?? j?["userPrincipalName"]?.GetValue<string>() ?? "";
        return (email.Trim().ToLowerInvariant(), j?["displayName"]?.GetValue<string>() ?? "");
    }
}

/// <summary>Matches email addresses to vendors by domain (subdomains included) or exact address.</summary>
public sealed class VendorMatcher
{
    private readonly Dictionary<string, int> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _domains = new(StringComparer.OrdinalIgnoreCase);

    public VendorMatcher(IEnumerable<Vendor> vendors)
    {
        foreach (var v in vendors.Where(v => v.IsActive))
            foreach (var d in v.Domains)
                (d.Contains('@') ? _addresses : _domains).TryAdd(d, v.VendorId);
    }

    public bool Empty => _addresses.Count == 0 && _domains.Count == 0;

    public int? Match(string address)
    {
        if (_addresses.TryGetValue(address, out var id)) return id;
        var domain = address[(address.LastIndexOf('@') + 1)..];
        for (var d = domain; d.Length > 0; d = d.Contains('.') ? d[(d.IndexOf('.') + 1)..] : "")
            if (_domains.TryGetValue(d, out id)) return id;
        return null;
    }

    /// <summary>Free mail providers can't be a vendor domain (that would track personal mail); add the person's full address instead.</summary>
    public static readonly HashSet<string> FreeMail = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.in", "ymail.com", "outlook.com", "hotmail.com", "live.com", "msn.com",
        "aol.com", "icloud.com", "me.com", "proton.me", "protonmail.com", "zoho.com", "rediffmail.com", "gmx.com", "mail.com",
    };
}

public sealed record SyncResult(int Read, int Tracked, string Status, string? Error);

/// <summary>Reads new mail from linked Outlook mailboxes through Microsoft Graph and keeps a summary of vendor emails.</summary>
public sealed class MailSync(IServiceScopeFactory scopes, IHttpClientFactory http, IOptions<MicrosoftOptions> options,
    IOptions<FollowUpOptions> followUps, IDataProtectionProvider dp, ILogger<MailSync> log) : BackgroundService
{
    private readonly Channel<int> _kicks = Channel.CreateUnbounded<int>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, (string Token, DateTime ExpiresUtc)> _access = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _tokenLocks = new();

    public static bool CanSend(string? grantedScopes) =>
        grantedScopes?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(s => s.EndsWith("Mail.Send", StringComparison.OrdinalIgnoreCase)) ?? false;

    /// <summary>Forget a cached Graph token (after disconnect or a new sign-in).</summary>
    public void Forget(int userId) => _access.TryRemove(userId, out _);

    /// <summary>
    /// A Graph access token for the mailbox: cached until 5 minutes before expiry, otherwise redeemed from the saved refresh token
    /// (Microsoft rotates it, so the new one is saved). Throws AppException MAIL_RECONNECT when the user has to connect again.
    /// </summary>
    public async Task<string> AccessTokenAsync(AppDbContext db, MicrosoftAuth auth, MailConnection conn, CancellationToken ct)
    {
        if (_access.TryGetValue(conn.UserId, out var hit) && hit.ExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return hit.Token;
        var gate = _tokenLocks.GetOrAdd(conn.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_access.TryGetValue(conn.UserId, out hit) && hit.ExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return hit.Token;
            await db.Entry(conn).ReloadAsync(ct);   // another request may have rotated the refresh token
            string refresh;
            try { refresh = Protector.Unprotect(conn.RefreshTokenProtected); }
            catch (CryptographicException)
            {
                throw new AppException(401, "MAIL_RECONNECT", "Outlook needs to be reconnected", "The saved Outlook sign-in can't be read on this server. Connect Outlook again.");
            }
            var t = await auth.RefreshAsync(refresh, ct);
            if (t.RefreshToken is { } rotated) conn.RefreshTokenProtected = Protector.Protect(rotated);
            if (t.Scope is { Length: > 0 } sc) conn.GrantedScopes = sc.Length > 1000 ? sc[..1000] : sc;
            await db.SaveChangesAsync(ct);
            _access[conn.UserId] = (t.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, t.ExpiresIn)));
            return t.AccessToken;
        }
        finally { gate.Release(); }
    }
    private IDataProtector Protector => dp.CreateProtector("MailConnections.RefreshToken.v1");

    public string Protect(string refreshToken) => Protector.Protect(refreshToken);

    /// <summary>Ask for a sync soon (after sign-in, connect or vendor changes). 0 = every mailbox.</summary>
    public void Kick(int userId = 0) => _kicks.Writer.TryWrite(userId);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        if (!o.Configured) { log.LogInformation("Outlook mail tracking is off (Microsoft:TenantId, ClientId and ClientSecret are not set)."); return; }
        var every = TimeSpan.FromMinutes(Math.Max(1, o.MailSyncMinutes));
        var next = DateTime.UtcNow.AddSeconds(30);
        while (!stop.IsCancellationRequested)
        {
            int? only = null;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var delay = next - DateTime.UtcNow;
                if (delay > TimeSpan.Zero) wait.CancelAfter(delay);
                only = await _kicks.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { /* timer */ }
            catch (OperationCanceledException) { return; }

            try
            {
                if (only is int id and > 0) await SyncAsync(id, stop);
                else { await SyncAllAsync(stop); next = DateTime.UtcNow + every; }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Mail sync failed"); }
        }
    }

    private async Task SyncAllAsync(CancellationToken ct)
    {
        List<int> ids;
        using (var scope = scopes.CreateScope())
            ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().MailConnections
                .Where(c => c.Status == "Active").Select(c => c.UserId).ToListAsync(ct);
        foreach (var id in ids) await SyncAsync(id, ct);
    }

    /// <summary>Syncs one mailbox. Never throws for mailbox problems; they're saved on the connection for the user to see.</summary>
    public async Task<SyncResult> SyncAsync(int userId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);   // one sync at a time keeps Graph throttling and token rotation simple
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var auth = scope.ServiceProvider.GetRequiredService<MicrosoftAuth>();
            var conn = await db.MailConnections.FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (conn is null) return new SyncResult(0, 0, "NotConnected", null);

            string accessToken;
            try { accessToken = await AccessTokenAsync(db, auth, conn, ct); }
            catch (AppException ex) { return await FailAsync(db, conn, ex.Code == "MAIL_RECONNECT" ? "ReconnectNeeded" : conn.Status, ex.Message, ct); }

            var vendors = new VendorMatcher(await db.Vendors.AsNoTracking().Where(v => v.IsActive).ToListAsync(ct));
            var o = options.Value;
            var since = (conn.SyncedThroughUtc ?? DateTime.UtcNow.AddDays(-Math.Max(1, o.FirstSyncDays))).AddMinutes(-5);
            var own = new HashSet<string>(o.CompanyDomains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
            if (conn.Mailbox.Contains('@')) own.Add(conn.Mailbox[(conn.Mailbox.IndexOf('@') + 1)..]);

            List<JsonNode> messages;
            try { messages = await ReadMessagesAsync(accessToken, since, o.MaxMessagesPerSync, ct); }
            catch (AppException ex)
            {
                if (ex.Status == 401) Forget(userId);
                return await FailAsync(db, conn, ex.Status == 401 ? "ReconnectNeeded" : conn.Status, ex.Message, ct);
            }

            var tracked = 0;
            if (!vendors.Empty && messages.Count > 0)
            {
                var consultants = await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(c => c.Status != "Inactive")
                    .Select(c => new { c.ConsultantId, c.FullName }).ToListAsync(ct);
                var names = consultants.Where(c => c.FullName.Trim().Contains(' '))
                    .Select(c => (c.ConsultantId, Re: new Regex($@"\b{Regex.Escape(c.FullName.Trim())}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), c.FullName.Length))
                    .OrderByDescending(c => c.Length).ToList();
                var ids = messages.Select(m => Str(m["id"])).OfType<string>().ToList();
                var seen = new HashSet<string>();
                foreach (var chunk in ids.Chunk(200))
                {
                    var c = chunk.ToList();
                    seen.UnionWith(await db.VendorEmails.Where(e => e.UserId == userId && c.Contains(e.MessageId)).Select(e => e.MessageId).ToListAsync(ct));
                }

                foreach (var m in messages)
                {
                    var id = Str(m["id"]);
                    if (id is null || id.Length > 400 || seen.Contains(id) || m["isDraft"]?.GetValue<bool>() == true) continue;
                    var from = Addr(m["from"]);
                    var to = Addrs(m["toRecipients"]).Concat(Addrs(m["ccRecipients"])).ToList();
                    if (from is null) continue;
                    var outgoing = from.Value.Address.Equals(conn.Mailbox, StringComparison.OrdinalIgnoreCase) || own.Contains(Domain(from.Value.Address));
                    var vendorId = outgoing ? to.Select(a => vendors.Match(a.Address)).FirstOrDefault(v => v != null) : vendors.Match(from.Value.Address);
                    if (vendorId is null) continue;

                    var subject = Cut(Str(m["subject"]) ?? "(no subject)", 400);
                    var preview = Cut(Regex.Replace(Str(m["bodyPreview"]) ?? "", @"\s+", " ").Trim(), 200);
                    var text = subject + " " + preview;
                    db.VendorEmails.Add(new VendorEmail
                    {
                        UserId = userId, VendorId = vendorId.Value, MessageId = id, ConversationId = Str(m["conversationId"]) is { Length: <= 400 } cid ? cid : null,
                        Direction = outgoing ? "Out" : "In", FromAddress = Cut(from.Value.Address, 256), FromName = from.Value.Name is { } fn ? Cut(fn, 200) : null,
                        ToAddresses = Cut(string.Join(", ", to.Select(a => a.Address)), 1000), Subject = subject, Preview = preview,
                        Attachments = ((m["attachments"] as JsonArray) ?? []).Select(a => Str(a?["name"])).OfType<string>().Take(20).Select(n => Cut(n, 200)).ToList(),
                        SentUtc = Date(m["sentDateTime"]) ?? Date(m["receivedDateTime"]) ?? DateTime.UtcNow,
                        ConsultantId = names.FirstOrDefault(n => n.Re.IsMatch(text)).ConsultantId is int cId and > 0 ? cId : null,
                    });
                    seen.Add(id);
                    tracked++;
                }
            }

            var newest = messages.Select(m => Date(m["receivedDateTime"])).Where(d => d != null).DefaultIfEmpty(null).Max();
            // Only move the watermark forward when every message up to "newest" was read (not cut off by MaxMessagesPerSync mid-page).
            conn.SyncedThroughUtc = messages.Count >= o.MaxMessagesPerSync ? newest ?? conn.SyncedThroughUtc : DateTime.UtcNow;
            conn.LastSyncUtc = DateTime.UtcNow;
            conn.Status = "Active";
            conn.LastError = null;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (SqlErrors.IsDuplicate(e)) { log.LogInformation("Mail sync for user {User} overlapped another sync; it will catch up next run.", userId); }
            if (tracked > 0) log.LogInformation("Mail sync for user {User}: read {Read}, tracked {Tracked} vendor emails", userId, messages.Count, tracked);
            try { await FollowUps.ReconcileAsync(db, userId, followUps.Value, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Follow-up reminders for user {User} not updated", userId); }
            return new SyncResult(messages.Count, tracked, "Active", null);
        }
        finally { _gate.Release(); }
    }

    private static async Task<SyncResult> FailAsync(AppDbContext db, MailConnection conn, string status, string error, CancellationToken ct)
    {
        conn.Status = status;
        conn.LastError = error.Length > 500 ? error[..500] : error;
        conn.LastSyncUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new SyncResult(0, 0, status, error);
    }

    private async Task<List<JsonNode>> ReadMessagesAsync(string accessToken, DateTime since, int max, CancellationToken ct)
    {
        // /me/messages covers every folder, including Sent Items. Oldest first so the watermark only moves forward.
        var url = $"{options.Value.GraphUrl.TrimEnd('/')}/v1.0/me/messages"
                  + "?$select=id,conversationId,subject,bodyPreview,from,toRecipients,ccRecipients,sentDateTime,receivedDateTime,isDraft,hasAttachments"
                  + "&$expand=attachments($select=name)"
                  + $"&$filter=receivedDateTime ge {since:yyyy-MM-ddTHH:mm:ssZ}&$orderby=receivedDateTime asc&$top=50";
        var list = new List<JsonNode>();
        var client = http.CreateClient("microsoft");
        while (url != null && list.Count < max)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new("Bearer", accessToken);
            req.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
            using var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AppException(401, "MAIL_RECONNECT", "Outlook needs to be reconnected", "Outlook refused access to the mailbox. Connect Outlook again and accept the mail permission.");
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Graph messages {Status}: {Body}", (int)res.StatusCode, body.Length > 300 ? body[..300] : body);
                throw new AppException(502, "MAIL_ERROR", "Couldn't read Outlook", (int)res.StatusCode == 429 ? "Outlook is busy. The next sync will try again." : "Outlook returned an error. The next sync will try again.");
            }
            var j = JsonNode.Parse(body);
            list.AddRange(((j?["value"] as JsonArray) ?? []).OfType<JsonNode>());
            url = Str(j?["@odata.nextLink"]);
        }
        return list.Take(max).ToList();
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    private static DateTime? Date(JsonNode? n) => Str(n) is { } s && DateTimeOffset.TryParse(s, out var d) ? d.UtcDateTime : null;
    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
    private static string Domain(string address) => address[(address.LastIndexOf('@') + 1)..];
    private static (string Address, string? Name)? Addr(JsonNode? n) =>
        Str(n?["emailAddress"]?["address"]) is { } a && a.Contains('@') ? (a.Trim().ToLowerInvariant(), Str(n?["emailAddress"]?["name"])) : null;
    private static IEnumerable<(string Address, string? Name)> Addrs(JsonNode? n) =>
        ((n as JsonArray) ?? []).Select(Addr).Where(a => a != null).Select(a => a!.Value);
}

public sealed record OutgoingAttachment(string Name, string ContentType, byte[] Bytes);
public sealed record OutgoingMail(List<string> To, List<string> Cc, string Subject, string Body, List<OutgoingAttachment> Attachments, string? ReplyToMessageId);

/// <summary>Sends mail as the signed-in user through Microsoft Graph, so it lands in their own Sent Items and the vendor's thread.</summary>
public sealed class MailSender(AppDbContext db, MicrosoftAuth auth, MailSync sync, IHttpClientFactory http, ILogger<MailSender> log)
{
    public async Task SendAsync(int userId, OutgoingMail mail, CancellationToken ct)
    {
        var conn = await db.MailConnections.FirstOrDefaultAsync(c => c.UserId == userId, ct)
                   ?? throw AppException.Conflict("MAIL_NOT_CONNECTED", "Outlook isn't connected", "Connect your Outlook mailbox to send email from the portal.");
        if (conn.Status != "Active")
            throw AppException.Conflict("MAIL_RECONNECT", "Outlook needs to be reconnected", conn.LastError ?? "Connect Outlook again, then send.");
        if (!MailSync.CanSend(conn.GrantedScopes))
            throw AppException.Conflict("MAIL_SEND_CONSENT", "Sending isn't allowed yet", "Reconnect Outlook and accept the \"send mail\" permission to send email from the portal.");

        var token = await sync.AccessTokenAsync(db, auth, conn, ct);
        var client = http.CreateClient("microsoft");
        var graph = auth.Graph + "/v1.0/me";
        static JsonArray Recipients(IEnumerable<string> a) => new(a.Select(x => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = x } }).ToArray());
        static JsonObject Attachment(OutgoingAttachment a) => new()
        {
            ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = a.Name, ["contentType"] = a.ContentType, ["contentBytes"] = Convert.ToBase64String(a.Bytes),
        };

        if (mail.ReplyToMessageId is null)
        {
            var message = new JsonObject
            {
                ["subject"] = mail.Subject,
                ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = mail.Body },
                ["toRecipients"] = Recipients(mail.To), ["ccRecipients"] = Recipients(mail.Cc),
                ["attachments"] = new JsonArray(mail.Attachments.Select(a => (JsonNode)Attachment(a)).ToArray()),
            };
            await GraphAsync(client, token, HttpMethod.Post, $"{graph}/sendMail", new JsonObject { ["message"] = message, ["saveToSentItems"] = true }, userId, ct);
        }
        else
        {
            // Reply inside the vendor's thread: draft reply (quotes the original), set recipients, attach files, send.
            var draft = await GraphAsync(client, token, HttpMethod.Post, $"{graph}/messages/{Uri.EscapeDataString(mail.ReplyToMessageId)}/createReply",
                new JsonObject { ["comment"] = mail.Body }, userId, ct);
            var draftId = draft?["id"]?.GetValue<string>() ?? throw new AppException(502, "MAIL_ERROR", "Couldn't send the email", "Outlook didn't create the reply. Try again.");
            try
            {
                await GraphAsync(client, token, HttpMethod.Patch, $"{graph}/messages/{Uri.EscapeDataString(draftId)}",
                    new JsonObject { ["toRecipients"] = Recipients(mail.To), ["ccRecipients"] = Recipients(mail.Cc) }, userId, ct);
                foreach (var a in mail.Attachments)
                    await GraphAsync(client, token, HttpMethod.Post, $"{graph}/messages/{Uri.EscapeDataString(draftId)}/attachments", Attachment(a), userId, ct);
                await GraphAsync(client, token, HttpMethod.Post, $"{graph}/messages/{Uri.EscapeDataString(draftId)}/send", null, userId, ct);
            }
            catch
            {
                // Don't leave a half-built draft in the user's mailbox.
                try { await GraphAsync(client, token, HttpMethod.Delete, $"{graph}/messages/{Uri.EscapeDataString(draftId)}", null, userId, CancellationToken.None); }
                catch (AppException) { /* best effort */ }
                throw;
            }
        }
        sync.Kick(userId);   // the sent email shows up in Vendor Emails on the next quick sync
    }

    private async Task<JsonNode?> GraphAsync(HttpClient client, string token, HttpMethod method, string url, JsonNode? body, int userId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new("Bearer", token);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode) return text.Length > 0 ? JsonNode.Parse(text) : null;

        var code = "";
        // The error code is only used to pick a clearer message; an unreadable body falls through to the status-code handling.
        try { code = JsonNode.Parse(text)?["error"]?["code"]?.GetValue<string>() ?? ""; } catch (System.Text.Json.JsonException) { }
        log.LogWarning("Graph {Method} {Path} failed {Status} {Code}", method, new Uri(url).AbsolutePath, (int)res.StatusCode, code);
        if (res.StatusCode == HttpStatusCode.Unauthorized) { sync.Forget(userId); throw new AppException(409, "MAIL_RECONNECT", "Outlook needs to be reconnected", "Outlook refused the request. Connect Outlook again."); }
        if (res.StatusCode == HttpStatusCode.Forbidden) throw new AppException(409, "MAIL_SEND_CONSENT", "Sending isn't allowed", "Outlook didn't allow sending. Reconnect Outlook and accept the \"send mail\" permission, or ask your admin to grant consent.");
        if (res.StatusCode == HttpStatusCode.NotFound) throw new AppException(404, "MAIL_ORIGINAL_GONE", "Original email not found", "The email you're replying to was deleted or moved out of the mailbox. Send a new email instead.");
        if ((int)res.StatusCode == 429) throw new AppException(429, "MAIL_THROTTLED", "Outlook is busy", "Outlook is limiting requests right now. Wait a minute and try again.");
        if (res.StatusCode == HttpStatusCode.RequestEntityTooLarge || code == "ErrorMessageSizeExceeded")
            throw new AppException(413, "MAIL_TOO_LARGE", "Attachments too large", "Outlook rejected the email size. Keep attachments under 3 MB in total.");
        throw new AppException(502, "MAIL_ERROR", "Couldn't send the email", "Outlook returned an error. Try again in a minute.");
    }
}
