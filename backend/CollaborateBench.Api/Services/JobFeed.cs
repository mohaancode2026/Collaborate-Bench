using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Services;

/// <summary>"JobFeed" section of appsettings. Two sources: Dice (free, on by default) and JSearch (Indeed, LinkedIn, Glassdoor…; needs RapidApiKey).</summary>
public sealed class JobFeedOptions
{
    public string Provider { get; set; } = "JSearch";
    /// <summary>Search Dice through its public MCP server. No key needed.</summary>
    public bool DiceEnabled { get; set; } = true;
    public string DiceMcpUrl { get; set; } = "https://mcp.dice.com/mcp";
    public int DiceJobsPerSearch { get; set; } = 25;
    /// <summary>Fetch each Dice job's full description and skills (one extra call per job) for better matching.</summary>
    public bool DiceDetails { get; set; } = true;
    /// <summary>RapidAPI key with a JSearch subscription. Keep it in user secrets or environment variables, not in source control.</summary>
    public string RapidApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://jsearch.p.rapidapi.com/";
    public string Host { get; set; } = "jsearch.p.rapidapi.com";
    /// <summary>JSearch calls per day (UTC) for the whole company, manual and automatic together. Dice searches don't count.</summary>
    public int DailyCallLimit { get; set; } = 50;
    /// <summary>The same search within this many hours reuses the saved results instead of calling the API.</summary>
    public int CacheHours { get; set; } = 6;
    /// <summary>0 = off. Otherwise searches for every bench consultant's title and location this often.</summary>
    public int AutoRefreshHours { get; set; } = 0;
    public int AutoRefreshMaxSearches { get; set; } = 10;
    /// <summary>Jobs not seen again for this many days (and never imported) are deleted.</summary>
    public int KeepDays { get; set; } = 30;
    public bool JSearchConfigured => !string.IsNullOrWhiteSpace(RapidApiKey);
    /// <summary>At least one source can be searched.</summary>
    public bool Configured => DiceEnabled || JSearchConfigured;
    /// <summary>The sources a search uses, e.g. ["Dice", "JSearch"].</summary>
    public List<string> Sources => [.. DiceEnabled ? new[] { "Dice" } : [], .. JSearchConfigured ? new[] { Provider } : []];
    /// <summary>For messages and the audit log: "Dice and JSearch (Indeed, LinkedIn, Glassdoor)".</summary>
    public string SourceLabel => string.Join(" and ", Sources.Select(s => s == "Dice" ? "Dice" : $"{s} (Indeed, LinkedIn, Monster, Glassdoor)"));
}

public sealed record FetchedJob(string ExternalId, string Title, string Company, string Location, bool Remote, string? EmploymentType,
    string? Publisher, string ApplyUrl, List<ApplyOption> ApplyOptions, string? Description, decimal? MinHourly, decimal? MaxHourly, DateTime? PostedUtc, string SkillText);

/// <summary>JSearch on RapidAPI: Google for Jobs results, which include listings from LinkedIn, Indeed, Glassdoor, ZipRecruiter, Dice and company sites.</summary>
public sealed class JSearchClient(IHttpClientFactory factory, IOptions<JobFeedOptions> options, ILogger<JSearchClient> log)
{
    public async Task<List<FetchedJob>> SearchAsync(string query, string? location, bool remoteOnly, string datePosted, bool contractOnly, CancellationToken ct)
    {
        var o = options.Value;
        var text = string.IsNullOrWhiteSpace(location) ? query : $"{query} in {location}";
        var url = $"search?query={Uri.EscapeDataString(text)}&page=1&num_pages=1&country=us&date_posted={datePosted}";
        if (contractOnly) url += "&employment_types=CONTRACTOR";
        if (remoteOnly) url += "&work_from_home=true&remote_jobs_only=true";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-RapidAPI-Key", o.RapidApiKey);
        req.Headers.Add("X-RapidAPI-Host", o.Host);
        HttpResponseMessage res;
        try { res = await factory.CreateClient("jobfeed").SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AppException(504, "JOBFEED_TIMEOUT", "Job search timed out", "The job search service took too long to answer. Try again in a minute.");
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Job search API unreachable");
            throw new AppException(503, "JOBFEED_UNAVAILABLE", "Job search unavailable", "Couldn't reach the job search service. Check the server's internet connection and try again.");
        }

        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                log.LogError("Job search API rejected the key ({Status}): {Body}", (int)res.StatusCode, Cut(body, 300));
                throw new AppException(502, "JOBFEED_AUTH", "Job search key rejected",
                    "The job search API key was rejected. Ask your admin to check JobFeed:RapidApiKey and that the JSearch subscription is active.");
            }
            if ((int)res.StatusCode == 429)
                throw new AppException(429, "JOBFEED_PROVIDER_LIMIT", "Job search plan limit reached",
                    "The job search provider's limit for this plan has been reached. Try again later, or ask your admin to upgrade the plan.");
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Job search API {Status}: {Body}", (int)res.StatusCode, Cut(body, 500));
                throw new AppException(502, "JOBFEED_ERROR", "Job search failed", "The job search service returned an error. Try again in a minute.");
            }

            JsonArray? data;
            try { data = JsonNode.Parse(body)?["data"] as JsonArray; }
            catch (System.Text.Json.JsonException)
            {
                log.LogWarning("Job search API returned invalid JSON: {Body}", Cut(body, 300));
                throw new AppException(502, "JOBFEED_ERROR", "Job search failed", "The job search service returned something unreadable. Try again in a minute.");
            }
            return (data ?? []).Select(Parse).OfType<FetchedJob>().ToList();
        }
    }

    private static FetchedJob? Parse(JsonNode? j)
    {
        if (j is null) return null;
        var id = S(j["job_id"]);
        var title = S(j["job_title"]);
        // Only http(s) links ever reach the browser.
        var options = ((j["apply_options"] as JsonArray) ?? [])
            .Select(o => (Publisher: S(o?["publisher"]), Url: S(o?["apply_link"]), Direct: B(o?["is_direct"])))
            .Where(o => o.Publisher != null && SafeUrl(o.Url))
            .GroupBy(o => o.Url).Select(g => g.First()).Take(8)
            .Select(o => new ApplyOption(Cut(o.Publisher!, 100), o.Url!, o.Direct)).ToList();
        var apply = S(j["job_apply_link"]) is { } a && SafeUrl(a) ? a : options.FirstOrDefault()?.Url;
        if (id is null || id.Length > 200 || title is null || apply is null) return null;

        var remote = B(j["job_is_remote"]);
        var city = S(j["job_city"]);
        var state = S(j["job_state"]);
        var location = city != null && state != null ? $"{city}, {state}" : S(j["job_location"]) ?? state ?? (remote ? "Remote" : S(j["job_country"]) ?? "");
        var period = S(j["job_salary_period"]);
        var posted = S(j["job_posted_at_datetime_utc"]) is { } p && DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) ? dto.UtcDateTime
            : D(j["job_posted_at_timestamp"]) is { } ts and > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)ts).UtcDateTime : (DateTime?)null;
        var description = S(j["job_description"]);
        var quals = (j["job_highlights"]?["Qualifications"] as JsonArray)?.Select(S).OfType<string>() ?? [];
        var skills = (j["job_required_skills"] as JsonArray)?.Select(S).OfType<string>() ?? [];

        return new FetchedJob(id, Cut(title, 300), Cut(S(j["employer_name"]) ?? "Confidential", 200), Cut(location, 200), remote,
            S(j["job_employment_type"]) is { } et ? Cut(et, 40) : null, S(j["job_publisher"]) is { } pub ? Cut(pub, 100) : null, apply, options,
            description is null ? null : Cut(description, 20000), Hourly(D(j["job_min_salary"]), period), Hourly(D(j["job_max_salary"]), period), posted,
            string.Join("\n", [title, .. quals, .. skills, description ?? ""]));
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static bool B(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static decimal? D(JsonNode? n) => n is not JsonValue v ? null
        : v.TryGetValue<decimal>(out var d) ? d
        : v.TryGetValue<double>(out var x) ? (decimal)x
        : v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : null;
    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
    private static bool SafeUrl(string? url) =>
        url is { Length: <= 2000 } && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    /// <summary>Salary → $/hr (2,080 working hours a year). Values outside $10–$500/hr are treated as unknown.</summary>
    internal static decimal? Hourly(decimal? amount, string? period)
    {
        if (amount is not > 0) return null;
        decimal? h = period?.ToUpperInvariant() switch
        {
            "HOUR" => amount, "DAY" => amount / 8, "WEEK" => amount / 40, "MONTH" => amount * 12 / 2080, "YEAR" => amount / 2080, _ => null,
        };
        return h is >= 10 and <= 500 ? Math.Round(h.Value, 2) : null;
    }
}

/// <summary>Reads skills, visas, contract type and work mode out of an external job's text.</summary>
public static class JobText
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly (string Visa, string Pattern)[] VisaPatterns =
    [
        ("USC", @"\bU\.?S\.? citizens?\b|\bUSC\b"), ("GC", @"\bgreen\s*card\b|\bGC\b(?![- ]?EAD)"), ("GC-EAD", @"\bGC[- ]?EAD\b"),
        ("H1B", @"\bH-?1-?B\b"), ("H4-EAD", @"\bH-?4[- ]?EAD\b"), ("L2-EAD", @"\bL-?2[- ]?EAD\b"), ("STEM OPT", @"\bSTEM[- ]OPT\b"),
        ("OPT", @"(?<!STEM[- ])\bOPT\b"), ("CPT", @"\bCPT\b"), ("TN", @"\bTN\s+visa\b"),
    ];
    private const string Negation = @"\b(no|not|non|without|cannot\s+accept|can't\s+accept)[\s-]+(\w+[\s/,]+){0,2}?";
    private static readonly string[] NeedSponsorship = ["H1B", "OPT", "CPT", "STEM OPT"];

    public static List<string> Skills(string text) => Rules.FindSkills(text).Take(15).ToList();

    /// <summary>Accepted visas, or ["Any"] when the posting doesn't say.</summary>
    public static List<string> Visas(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ["Any"];
        var negated = VisaPatterns.Where(v => Regex.IsMatch(text, Negation + "(" + v.Pattern + ")", I)).Select(v => v.Visa).ToHashSet();
        if (Regex.IsMatch(text, @"\b(no|not|unable\s+to|cannot|can't|will\s+not|won't|without)\s+(\w+\s+){0,3}?(visa\s+)?sponsor", I))
            negated.UnionWith(NeedSponsorship);
        var mentioned = VisaPatterns.Where(v => !negated.Contains(v.Visa) && Regex.IsMatch(text, v.Pattern, I)).Select(v => v.Visa).ToList();

        if (Regex.IsMatch(text, @"must\s+be\s+(a\s+)?U\.?S\.?\s+citizen|U\.?S\.?\s+citizenship\s+(is\s+)?required|(security|secret|public\s+trust)\s+clearance", I))
            return mentioned.Contains("GC") ? ["USC", "GC"] : ["USC"];
        if (mentioned.Count > 0) return mentioned;
        if (negated.Count > 0) return Domain.Visas.Where(v => !negated.Contains(v)).ToList();
        return ["Any"];
    }

    public static string SuggestedType(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "C2C";
        var noC2c = Regex.IsMatch(text, @"\bno\s+(C2C|corp[- ]to[- ]corp)", I);
        if (!noC2c && Regex.IsMatch(text, @"\bC2C\b|corp[- ]to[- ]corp", I)) return "C2C";
        if (Regex.IsMatch(text, @"\bW-?2\b", I)) return "W2";
        if (Regex.IsMatch(text, @"\b1099\b")) return "1099";
        return noC2c ? "W2" : "C2C";
    }

    public static string Mode(ExternalJob j) =>
        j.IsRemote ? "Remote" : Regex.IsMatch(j.Description ?? "", @"\bhybrid\b", I) ? "Hybrid" : "Onsite";

    /// <summary>The job as a requirement, so the normal match score works on it.</summary>
    public static RequirementDto AsRequirement(ExternalJob j) => new()
    {
        Id = j.RequirementId ?? 0, Title = j.Title, Client = j.Company, Vendor = j.Publisher ?? "Job board", Location = j.Location,
        Mode = Mode(j), Type = SuggestedType(j.Description), Rate = j.MaxHourly ?? 0, Skills = j.Skills, Visas = j.Visas, Status = "Open",
    };

    /// <summary>What to search for a consultant: their top skills (the first ones on the profile), optionally with their title.
    /// Falls back to the title when the profile has no skills. Never names or contact details.</summary>
    public static string QueryFor(string? title, IReadOnlyList<string> skills, int topSkills = 3, bool includeTitle = false)
    {
        var t = Regex.Replace(title ?? "", @"\s+", " ").Trim();
        var s = string.Join(" ", skills.Where(x => !string.IsNullOrWhiteSpace(x)).Take(topSkills));
        if (s.Length == 0) return t;
        return includeTitle && t.Length >= 2 ? $"{t} {s}" : s;
    }

    public static bool OnLinkedIn(ExternalJob j) => OnBoard(j, "linkedin");

    /// <summary>Listed on that job board: Dice jobs come from the Dice source; Indeed / LinkedIn by publisher or apply links.</summary>
    public static bool OnBoard(ExternalJob j, string board) =>
        j.Provider.Equals(board, StringComparison.OrdinalIgnoreCase)
        || (j.Publisher?.Contains(board, StringComparison.OrdinalIgnoreCase) ?? false)
        || j.ApplyOptions.Any(o => o.Publisher.Contains(board, StringComparison.OrdinalIgnoreCase));

    private static readonly Regex Email = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
    /// <summary>Recruiter emails written in the posting ("send resumes to ..."), common in C2C vendor posts.</summary>
    public static List<string> ContactEmails(string? text) => string.IsNullOrEmpty(text) ? []
        : Email.Matches(text).Select(m => m.Value.TrimEnd('.'))
            .Where(e => !e.StartsWith("noreply", StringComparison.OrdinalIgnoreCase) && !e.StartsWith("no-reply", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
}

/// <summary>Searches Dice and JSearch side by side, saves results (deduplicated per source), and enforces the cache and JSearch's daily limit.</summary>
public sealed class JobFeedService(AppDbContext db, JSearchClient client, DiceClient dice, IOptions<JobFeedOptions> options, ILogger<JobFeedService> log)
{
    public JobFeedOptions Options => options.Value;
    private static readonly string[] DatePostedValues = ["all", "today", "3days", "week", "month"];
    public static string NormalizeDate(string? d) => d != null && DatePostedValues.Contains(d) ? d : "week";

    /// <summary>Paid JSearch calls today. Dice is free, so Dice-only searches aren't counted.</summary>
    public Task<int> CallsTodayAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.Date;
        var jsearch = Options.Provider;
        return db.ExternalJobSearches.CountAsync(s => s.CreatedUtc >= since && !s.CacheHit && s.Provider.Contains(jsearch), ct);
    }

    public async Task<(ExternalJobSearch Search, int Added)> SearchAsync(string query, string? location, bool remoteOnly, string datePosted,
        bool contractOnly, int? userId, bool automatic, CancellationToken ct = default)
    {
        var o = Options;
        if (!o.Configured)
            throw new AppException(503, "JOBFEED_NOT_CONFIGURED", "Job search isn't set up",
                "Turn on JobFeed:DiceEnabled, or add a JSearch (RapidAPI) key under JobFeed:RapidApiKey, then restart the API.");

        query = Regex.Replace(query, @"\s+", " ").Trim();
        location = string.IsNullOrWhiteSpace(location) ? null : Regex.Replace(location, @"\s+", " ").Trim();
        var sources = o.Sources;
        var key = $"{string.Join('+', sources)}|{query.ToLowerInvariant()}|{location?.ToLowerInvariant()}|{remoteOnly}|{datePosted}|{contractOnly}";
        if (key.Length > 400) key = key[..400];

        var fresh = DateTime.UtcNow.AddHours(-o.CacheHours);
        var cached = await db.ExternalJobSearches.AsNoTracking()
            .Where(s => s.QueryKey == key && s.Succeeded && !s.CacheHit && s.CreatedUtc >= fresh)
            .OrderByDescending(s => s.CreatedUtc).FirstOrDefaultAsync(ct);
        var entry = new ExternalJobSearch
        {
            Provider = string.Join('+', sources), QueryKey = key, Query = query, Location = location, UserId = userId, Automatic = automatic,
        };
        if (cached != null)
        {
            entry.CacheHit = entry.Succeeded = true;
            entry.ResultCount = cached.ResultCount;
            entry.JobIds = cached.JobIds;
            db.ExternalJobSearches.Add(entry);
            await db.SaveChangesAsync(ct);
            return (entry, 0);
        }

        // JSearch is paid and capped per day; Dice is free. When JSearch is over its limit, Dice still runs.
        var useJSearch = o.JSearchConfigured;
        var errors = new List<AppException>();
        if (useJSearch && await CallsTodayAsync(ct) >= o.DailyCallLimit)
        {
            useJSearch = false;
            errors.Add(new AppException(429, "JOBFEED_DAILY_LIMIT", "Daily job search limit reached",
                $"The team has used all {o.DailyCallLimit} JSearch (Indeed, LinkedIn) searches for today. Saved jobs are still available, and the limit resets at midnight UTC."));
        }
        if (!useJSearch && !o.DiceEnabled) throw errors[0];
        if (!useJSearch) entry.Provider = "Dice";

        var calls = new List<(string Provider, Task<List<FetchedJob>> Task)>();
        if (o.DiceEnabled) calls.Add(("Dice", dice.SearchAsync(query, location, remoteOnly, datePosted, contractOnly, ct)));
        if (useJSearch) calls.Add((o.Provider, client.SearchAsync(query, location, remoteOnly, datePosted, contractOnly, ct)));
        var results = new List<(string Provider, List<FetchedJob> Jobs)>();
        foreach (var (provider, task) in calls)
        {
            try { results.Add((provider, await task)); }
            catch (AppException ex) { log.LogWarning("Job search \"{Query}\" on {Provider} failed: {Error}", query, provider, ex.Message); errors.Add(ex); }
        }

        if (errors.Count > 0)
        {
            var msg = string.Join(" ", errors.Select(e => e.Message));
            entry.Error = msg.Length > 500 ? msg[..500] : msg;
        }
        if (results.Count == 0)
        {
            db.ExternalJobSearches.Add(entry);
            await db.SaveChangesAsync(CancellationToken.None);
            throw errors[0];
        }

        var ids = new List<int>();
        var added = 0;
        foreach (var (provider, jobs) in results)
        {
            var (i, a) = await UpsertAsync(provider, jobs, ct);
            ids.AddRange(i); added += a;
        }
        entry.Succeeded = true;
        entry.ResultCount = ids.Count;
        entry.JobIds = ids;
        db.ExternalJobSearches.Add(entry);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Job search \"{Query}\" {Location} on {Sources}: {Count} jobs, {Added} new", query, location,
            string.Join(", ", results.Select(r => $"{r.Provider} {r.Jobs.Count}")), ids.Count, added);
        return (entry, added);
    }

    private async Task<(List<int> Ids, int Added)> UpsertAsync(string provider, List<FetchedJob> found, CancellationToken ct)
    {
        var jobs = found.GroupBy(f => f.ExternalId).Select(g => g.First()).ToList();
        for (var attempt = 1; ; attempt++)
        {
            var extIds = jobs.Select(f => f.ExternalId).ToList();
            var existing = extIds.Count == 0 ? new Dictionary<string, ExternalJob>()
                : await db.ExternalJobs.Where(j => j.Provider == provider && extIds.Contains(j.ExternalId)).ToDictionaryAsync(j => j.ExternalId, ct);
            var rows = new List<ExternalJob>();
            var added = 0;
            foreach (var f in jobs)
            {
                if (!existing.TryGetValue(f.ExternalId, out var row))
                {
                    row = new ExternalJob { Provider = provider, ExternalId = f.ExternalId };
                    db.ExternalJobs.Add(row);
                    added++;
                }
                // Refresh details every time the job shows up; keep RequirementId and IsHidden.
                row.Title = f.Title; row.Company = f.Company; row.Location = f.Location; row.IsRemote = f.Remote;
                row.EmploymentType = f.EmploymentType; row.Publisher = f.Publisher; row.ApplyUrl = f.ApplyUrl; row.ApplyOptions = f.ApplyOptions; row.Description = f.Description;
                row.MinHourly = f.MinHourly; row.MaxHourly = f.MaxHourly; row.PostedUtc = f.PostedUtc ?? row.PostedUtc;
                row.Skills = JobText.Skills(f.SkillText); row.Visas = JobText.Visas(f.Description);
                row.LastSeenUtc = DateTime.UtcNow;
                rows.Add(row);
            }
            try
            {
                await db.SaveChangesAsync(ct);
                return (rows.Select(r => r.ExternalJobId).ToList(), added);
            }
            catch (DbUpdateException e) when (attempt == 1 && SqlErrors.IsDuplicate(e))
            {
                // Another search saved the same job at the same moment: start over and update it instead.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Deletes jobs nobody imported that haven't shown up in any search for KeepDays, and old search logs.</summary>
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, Options.KeepDays));
        await db.ExternalJobs.Where(j => j.LastSeenUtc < cutoff && j.RequirementId == null).ExecuteDeleteAsync(ct);
        await db.ExternalJobSearches.Where(s => s.CreatedUtc < cutoff).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Optional: every AutoRefreshHours, searches for each bench consultant's title and location so matches are waiting for recruiters.</summary>
public sealed class JobFeedRefresher(IServiceScopeFactory scopes, IOptions<JobFeedOptions> options, ILogger<JobFeedRefresher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        if (!o.Configured || o.AutoRefreshHours <= 0)
        {
            log.LogInformation("External job auto-refresh is off (needs a job source and JobFeed:AutoRefreshHours > 0).");
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stop);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(o.AutoRefreshHours));
            do
            {
                try { await RunOnceAsync(stop); }
                catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "External job auto-refresh failed"); }
            }
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feed = scope.ServiceProvider.GetRequiredService<JobFeedService>();
        await feed.CleanupAsync(ct);

        // System job with no signed-in user: read every team's bench directly.
        var bench = await db.Consultants.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Status == "Bench" || c.Status == "Marketing")
            .Select(c => new { c.Title, c.Location, c.Skills }).ToListAsync(ct);
        var searches = bench
            .Select(c =>
            {
                var remote = string.IsNullOrWhiteSpace(c.Location) || c.Location.Contains("remote", StringComparison.OrdinalIgnoreCase);
                return (Query: JobText.QueryFor(c.Title, c.Skills), Location: remote ? null : c.Location!.Trim(), Remote: remote);
            })
            .GroupBy(s => (s.Query.ToLowerInvariant(), s.Location?.ToLowerInvariant(), s.Remote))
            .OrderByDescending(g => g.Count()).Select(g => g.First())
            .Take(Math.Max(1, options.Value.AutoRefreshMaxSearches)).ToList();

        var ran = 0;
        foreach (var s in searches)
        {
            try { await feed.SearchAsync(s.Query, s.Location, s.Remote, "3days", true, null, true, ct); ran++; }
            catch (AppException ex) when (ex.Code is "JOBFEED_DAILY_LIMIT" or "JOBFEED_PROVIDER_LIMIT" or "JOBFEED_AUTH")
            {
                log.LogWarning("External job auto-refresh stopped: {Reason}", ex.Message);
                break;
            }
            catch (AppException ex) { log.LogWarning("External job search \"{Query}\" failed: {Reason}", s.Query, ex.Message); }
        }
        log.LogInformation("External job auto-refresh ran {Count} of {Total} searches", ran, searches.Count);
    }
}
