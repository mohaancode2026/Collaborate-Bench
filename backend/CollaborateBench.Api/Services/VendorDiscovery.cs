using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Services;

public sealed record DiscoveredContact(string Address, string? Name, int Emails);

/// <summary>A company domain (or, for free mail, one person's address) found in the mailboxes that looks like a staffing vendor.</summary>
/// <param name="Key">The domain ("teksystems.com") or, for Gmail/Yahoo recruiters, the full address. This is what gets added to the vendor.</param>
/// <param name="LikelyConsultant">A personal address that mostly receives mail from you: probably a consultant, not a vendor.</param>
/// <param name="Score">0–100: how much the mail looks like bench-sales traffic (requirements, rates, C2C, RTR, two-way threads…).</param>
public sealed record DiscoveredVendor(string Key, bool IsAddress, bool LikelyConsultant, string SuggestedName, int Score, int Received, int Sent, int Mailboxes,
    DateTime LastSeenUtc, List<DiscoveredContact> Contacts, List<string> Signals, List<string> SampleSubjects);

public sealed record DiscoveryResult(int Mailboxes, int MessagesRead, int Days, List<DiscoveredVendor> Vendors, List<string> Problems);

/// <summary>
/// Scans connected Outlook mailboxes (sender, recipients, subject, first lines only) for outside companies that look like vendors.
/// Nothing is saved: the result is a list of suggestions that a Team Lead or admin confirms before anything is tracked.
/// </summary>
public sealed class VendorDiscovery(AppDbContext db, MicrosoftAuth auth, MailSync sync, IOptions<MicrosoftOptions> options, ILogger<VendorDiscovery> log)
{
    private const int MaxPerMailbox = 2000;

    /// <param name="userIds">Mailboxes to read (the caller's own, their team's, or everyone's, depending on their data scope).</param>
    public async Task<DiscoveryResult> ScanAsync(IReadOnlyCollection<int> userIds, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 90);
        var o = options.Value;
        var conns = await db.MailConnections.Where(c => userIds.Contains(c.UserId) && c.Status == "Active").ToListAsync(ct);
        var own = new HashSet<string>(o.CompanyDomains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        foreach (var c in conns.Where(c => c.Mailbox.Contains('@'))) own.Add(Registrable(Domain(c.Mailbox)));
        var mailboxes = new HashSet<string>(conns.Select(c => c.Mailbox), StringComparer.OrdinalIgnoreCase);

        // Domains and addresses that already belong to a vendor are left out of the suggestions.
        var known = (await db.Vendors.AsNoTracking().Select(v => v.Domains).ToListAsync(ct)).SelectMany(d => d).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Your own consultants are emailed a lot about jobs too; their addresses aren't vendors.
        var consultantEmails = (await db.Consultants.IgnoreQueryFilters().AsNoTracking().Where(c => c.Email != null).Select(c => c.Email!).ToListAsync(ct))
            .Select(e => e.Trim().ToLowerInvariant()).ToHashSet();

        var acc = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var since = DateTime.UtcNow.AddDays(-days);

        // Tokens one at a time (they use the database context), then read the mailboxes in parallel (plain HTTP).
        var tokens = new List<(MailConnection Conn, string Token)>();
        foreach (var conn in conns)
        {
            try { tokens.Add((conn, await sync.AccessTokenAsync(db, auth, conn, ct))); }
            catch (AppException ex) { Skip(conn, ex); }
        }
        using var gate = new SemaphoreSlim(4);
        var reads = await Task.WhenAll(tokens.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try { return (t.Conn, Messages: await sync.ReadHeadersAsync(t.Token, since, MaxPerMailbox, ct), Error: (AppException?)null); }
            catch (AppException ex) { return (t.Conn, Messages: new List<JsonNode>(), Error: ex); }
            finally { gate.Release(); }
        }));
        var read = 0;

        foreach (var (conn, messages, error) in reads)
        {
            if (error != null) { Skip(conn, error); continue; }
            read += messages.Count;

            foreach (var m in messages)
            {
                if (m["isDraft"]?.GetValue<bool>() == true || Addr(m["from"]) is not { } from) continue;
                var outgoing = mailboxes.Contains(from.Address) || own.Contains(Registrable(Domain(from.Address)));
                var parties = outgoing ? Addrs(m["toRecipients"]).Concat(Addrs(m["ccRecipients"])).ToList() : [from];
                var subject = Str(m["subject"]) ?? "";
                var text = subject + " " + (Str(m["bodyPreview"]) ?? "");
                var when = Str(m["receivedDateTime"]) is { } s && DateTimeOffset.TryParse(s, out var d) ? d.UtcDateTime : DateTime.UtcNow;
                var hits = Signals(text);

                foreach (var p in parties.DistinctBy(p => p.Address))
                {
                    if (IsNoise(p.Address, own) || consultantEmails.Contains(p.Address)) continue;
                    var domain = Registrable(Domain(p.Address));
                    var isFree = VendorMatcher.FreeMail.Contains(domain);
                    // A whole free-mail domain can't be a vendor; a Gmail recruiter only counts when the mail is clearly about jobs.
                    if (isFree && hits.Count < 2) continue;
                    var key = isFree ? p.Address : domain;
                    if (known.Contains(key) || known.Contains(Domain(p.Address))) continue;

                    if (!acc.TryGetValue(key, out var a)) acc[key] = a = new Acc(key, isFree);
                    if (outgoing) a.Sent++; else a.Received++;
                    a.Mailboxes.Add(conn.UserId);
                    if (when > a.LastSeen) a.LastSeen = when;
                    var contact = a.Contacts.TryGetValue(p.Address, out var ce) ? ce : a.Contacts[p.Address] = new ContactAcc();
                    contact.Count++;
                    contact.Name ??= p.Name;
                    foreach (var h in hits) a.Signals[h] = a.Signals.GetValueOrDefault(h) + 1;
                    if (hits.Count >= 2) a.SignalMessages++;
                    if (hits.Count > 0 && subject.Length > 0 && a.Subjects.Count < 3 && !a.Subjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
                        a.Subjects.Add(subject.Length > 140 ? subject[..140] : subject);
                }
            }
        }

        var vendors = acc.Values
            .Select(a => (a, score: Score(a)))
            .Where(x => x.score >= 25)
            .OrderBy(x => x.a.IsAddress).ThenByDescending(x => x.score).ThenByDescending(x => x.a.Received + x.a.Sent)
            .GroupBy(x => x.a.IsAddress).SelectMany(g => g.Take(g.Key ? 100 : 400))   // companies and personal addresses each get their own room
            .Select(x => new DiscoveredVendor(x.a.Key, x.a.IsAddress, LikelyConsultant(x.a), SuggestName(x.a), x.score, x.a.Received, x.a.Sent, x.a.Mailboxes.Count, x.a.LastSeen,
                x.a.Contacts.OrderByDescending(c => c.Value.Count).Take(5).Select(c => new DiscoveredContact(c.Key, c.Value.Name, c.Value.Count)).ToList(),
                x.a.Signals.OrderByDescending(s => s.Value).Take(6).Select(s => s.Key).ToList(),
                x.a.Subjects))
            .ToList();
        return new DiscoveryResult(conns.Count, read, days, vendors, problems);

        void Skip(MailConnection conn, AppException ex)
        {
            log.LogWarning("Vendor discovery skipped mailbox {Mailbox}: {Error}", conn.Mailbox, ex.Message);
            problems.Add($"{conn.Mailbox}: {ex.Message}");
        }
    }

    /// <summary>A personal address you mostly write to (resumes, RTRs, interview times) is usually one of your consultants, not a vendor.</summary>
    private static bool LikelyConsultant(Acc a) => a.IsAddress && a.Sent >= 3 * Math.Max(1, a.Received);

    // ---------- scoring ----------

    private sealed class Acc(string key, bool isAddress)
    {
        public string Key { get; } = key;
        public bool IsAddress { get; } = isAddress;
        public int Received, Sent, SignalMessages;
        public DateTime LastSeen = DateTime.MinValue;
        public HashSet<int> Mailboxes { get; } = [];
        public Dictionary<string, ContactAcc> Contacts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Signals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Subjects { get; } = [];
    }
    private sealed class ContactAcc { public int Count; public string? Name; }

    private static int Score(Acc a)
    {
        var total = a.Received + a.Sent;
        var s = Math.Min(45, a.SignalMessages * 9)                         // emails that read like requirements / submissions
              + Math.Min(15, a.Signals.Count * 3)                          // variety of bench-sales terms
              + (a.Received > 0 && a.Sent > 0 ? 20 : 0)                    // a real two-way conversation
              + (a.Contacts.Count > 1 ? Math.Min(10, (a.Contacts.Count - 1) * 5) : 0)
              + (total >= 10 ? 10 : total >= 3 ? 5 : 0)
              + (!a.IsAddress && VendorNameHint.IsMatch(a.Key) ? 10 : 0);
        // Mostly one-way inbound mail with no job terms is usually a newsletter or notification.
        if (a.Sent == 0 && a.SignalMessages == 0) s /= 2;
        return Math.Clamp(s, 0, 100);
    }

    /// <summary>Bench-sales vocabulary. The label is what's shown as evidence.</summary>
    private static readonly (string Label, Regex Re)[] Terms = new (string, string)[]
    {
        ("C2C", @"\bc2c\b|corp[\s-]*to[\s-]*corp"), ("W2", @"\bw-?2\b"), ("1099", @"\b1099\b"), ("C2H", @"\bc2h\b|contract[\s-]*to[\s-]*hire"),
        ("Requirement", @"\brequirements?\b|\breqs?\b"), ("Hotlist", @"\bhot\s*list\b|\bbench\b"), ("Rate", @"\brates?\b|\$\s?\d{2,3}\s*/\s*(hr|hour)|\bper\s+hour\b"),
        ("RTR", @"\brtr\b|right\s+to\s+represent"), ("Submission", @"\bsubmi(t|ssion|tted)\b"), ("Visa", @"\bh-?1b\b|\bgc\b|green\s*card|\busc\b|\bead\b|\bopt\b|\bcpt\b|\bvisa\b"),
        ("Contract", @"\bcontract\b|\bduration\b|\bmonths?\b\s+contract"), ("Client", @"end\s+client|\bimplementation\s+partner\b|\bprime\s+vendor\b|\bdirect\s+client\b|\bclient\b"),
        ("Location", @"\bonsite\b|\bon-site\b|\bremote\b|\bhybrid\b|\blocals?\s+only\b"), ("Opening", @"\bposition\b|\bopening\b|\brole\b|\bjob\b|\bjd\b|job\s+description|\burgent\b|\bimmediate\b"),
        ("Resume", @"\bresumes?\b|\bprofiles?\b|\bcandidates?\b|\bconsultants?\b"), ("Interview", @"\binterview\b|\bscreening\b"),
        ("Vendor", @"\bvendor\b|\bstaffing\b|\brecruiter\b|\btalent\b"),
    }.Select(t => (t.Item1, new Regex(t.Item2, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))).ToArray();

    private static List<string> Signals(string text) => Terms.Where(t => t.Re.IsMatch(text)).Select(t => t.Label).ToList();

    private static readonly Regex VendorNameHint = new(@"staff|tech|soft|solution|system|consult|infotech|global|talent|recruit|resource|labs|digital|data|cloud|\bit\b|it(\.|$)|corp|inc\b|group|services|partners",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---------- noise ----------

    private static readonly Regex NoiseLocal = new(@"^(no-?reply|do-?not-?reply|donotreply|notifications?|notify|alerts?|mailer-daemon|postmaster|bounces?|newsletters?|news|marketing|digest|updates?|calendar|jobalerts?|invitations?|automated|system|admin)([._+-].*)?$|^(.*[._-])?(no-?reply|noreply|bounce)([._+-].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Platforms, job boards, ATS and mail services: their mail isn't from a vendor even when it mentions jobs.</summary>
    private static readonly HashSet<string> NoiseDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft.com", "microsoftonline.com", "office.com", "office365.com", "sharepoint.com", "onmicrosoft.com", "microsoftsupport.com", "skype.com",
        "linkedin.com", "google.com", "googlegroups.com", "youtube.com", "facebook.com", "facebookmail.com", "instagram.com", "twitter.com", "x.com",
        "amazon.com", "amazonses.com", "amazonaws.com", "github.com", "atlassian.net", "atlassian.com", "slack.com", "zoom.us", "zoom.com", "webex.com",
        "docusign.net", "docusign.com", "adobe.com", "adobesign.com", "apple.com", "paypal.com", "intuit.com", "godaddy.com", "namecheap.com",
        "mailchimp.com", "mailchimpapp.net", "mcsv.net", "sendgrid.net", "sendgrid.com", "hubspot.com", "hubspotemail.net", "salesforce.com", "zendesk.com",
        "constantcontact.com", "mailgun.org", "sparkpostmail.com", "rsgsv.net", "list-manage.com", "substack.com", "medium.com", "quora.com",
        "indeed.com", "indeedemail.com", "dice.com", "monster.com", "ziprecruiter.com", "glassdoor.com", "naukri.com", "careerbuilder.com", "simplyhired.com",
        "ceipal.com", "jobdiva.com", "bullhorn.com", "bullhornstaffing.com", "jobvite.com", "greenhouse.io", "lever.co", "workday.com", "myworkday.com",
        "icims.com", "smartrecruiters.com", "taleo.net", "oracle.com", "zohorecruit.com", "zoho.com", "freshworks.com", "calendly.com", "grammarly.com",
        "canva.com", "dropbox.com", "box.com", "notion.so", "uber.com", "airbnb.com", "netflix.com", "spotify.com", "chase.com", "bankofamerica.com", "wellsfargo.com",
    };

    private static bool IsNoise(string address, HashSet<string> own)
    {
        var at = address.LastIndexOf('@');
        if (at <= 0) return true;
        var domain = Registrable(address[(at + 1)..]);
        return own.Contains(domain) || NoiseDomains.Contains(domain) || NoiseLocal.IsMatch(address[..at]);
    }

    // ---------- names and domains ----------

    private static readonly HashSet<string> SecondLevel = ["co", "com", "org", "net", "gov", "ac", "edu", "ltd", "plc"];

    /// <summary>mail.us.teksystems.com → teksystems.com; abc.co.in → abc.co.in.</summary>
    public static string Registrable(string domain)
    {
        var parts = domain.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        if (parts.Length <= 2) return string.Join('.', parts);
        var take = parts[^1].Length == 2 && SecondLevel.Contains(parts[^2]) ? 3 : 2;
        return string.Join('.', parts[^Math.Min(take, parts.Length)..]);
    }

    private static readonly string[] NameWords =
    [
        "technologies", "technology", "solutions", "systems", "consulting", "consultants", "staffing", "infotech", "software", "services", "resources",
        "partners", "global", "digital", "talent", "group", "labs", "cloud", "data", "tech", "soft", "info", "corp", "inc", "llc", "usa", "america", "it",
    ];

    private static string SuggestName(Acc a)
    {
        if (a.IsAddress)
        {
            var c = a.Contacts.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Name));
            return c?.Name?.Trim() is { Length: > 1 } n ? n : a.Key[..a.Key.IndexOf('@')];
        }
        var label = a.Key.Split('.')[0];
        // Split well-known trailing words: "apexsystems" → "Apex Systems", "kforceinc" → "Kforce Inc".
        var words = new List<string>();
        var rest = label;
        for (var guard = 0; guard < 4; guard++)
        {
            var w = NameWords.FirstOrDefault(w => rest.Length > w.Length + 1 && rest.EndsWith(w, StringComparison.OrdinalIgnoreCase));
            if (w is null) break;
            words.Insert(0, w);
            rest = rest[..^w.Length].TrimEnd('-');
        }
        words.Insert(0, rest);
        return string.Join(' ', words.SelectMany(w => w.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w is "it" or "usa" or "llc" ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string Domain(string address) => address[(address.LastIndexOf('@') + 1)..];
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    private static (string Address, string? Name)? Addr(JsonNode? n) =>
        Str(n?["emailAddress"]?["address"]) is { } a && a.Contains('@') ? (a.Trim().ToLowerInvariant(), Str(n?["emailAddress"]?["name"])) : null;
    private static IEnumerable<(string Address, string? Name)> Addrs(JsonNode? n) =>
        ((n as JsonArray) ?? []).Select(Addr).Where(a => a != null).Select(a => a!.Value);
}
