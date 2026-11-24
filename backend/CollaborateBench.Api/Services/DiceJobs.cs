using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace CollaborateBench.Api.Services;

/// <summary>
/// Dice job search through Dice's public MCP server (JSON-RPC over HTTP, no key needed). Dice has no other public API.
/// One search = one search_jobs call plus get_job_details for each result, so skills and visa terms come from the full posting.
/// </summary>
public sealed class DiceClient(IHttpClientFactory factory, IOptions<JobFeedOptions> options, ILogger<DiceClient> log)
{
    public async Task<List<FetchedJob>> SearchAsync(string query, string? location, bool remoteOnly, string datePosted, bool contractOnly, CancellationToken ct)
    {
        var o = options.Value;
        var args = new JsonObject
        {
            ["keyword"] = query,
            ["jobs_per_page"] = Math.Clamp(o.DiceJobsPerSearch, 5, 50),
            ["sort"] = "relevance",
        };
        if (!string.IsNullOrWhiteSpace(location)) { args["location"] = location; args["radius"] = 50; args["radius_unit"] = "mi"; }
        if (remoteOnly) args["workplace_types"] = new JsonArray("Remote");
        // Contract and third-party (C2C) roles are what bench sales places; full-time direct hires are left out.
        if (contractOnly) args["employment_types"] = new JsonArray("CONTRACTS", "THIRD_PARTY");
        if (datePosted switch { "today" => "ONE", "3days" => "THREE", "week" => "SEVEN", _ => null } is { } pd) args["posted_date"] = pd;

        var result = await CallAsync("search_jobs", args, ct);
        var data = result?["data"] as JsonArray ?? [];
        var jobs = data.OfType<JsonNode>().Select(Parse).OfType<DiceJob>().ToList();

        // Full description and skill list for each job, a few at a time. A failed detail call keeps the search summary.
        if (o.DiceDetails && jobs.Count > 0)
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(jobs.Select(async j =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var d = await CallAsync("get_job_details", new JsonObject { ["job_id"] = j.Guid }, ct);
                    j.Description = Text(S(d?["description"])) ?? j.Description;
                    j.Skills = ((d?["skills"] as JsonArray) ?? []).Select(s => S(s?["name"])).OfType<string>().Take(30).ToList();
                }
                catch (AppException ex) { log.LogDebug("Dice job {Guid} details skipped: {Error}", j.Guid, ex.Message); }
                finally { gate.Release(); }
            }));
        }
        return jobs.Select(j => j.ToFetched()).ToList();
    }

    private sealed class DiceJob
    {
        public string Guid = "", Title = "", Company = "", Location = "", ApplyUrl = "";
        public string? EmploymentType, EmployerType, Salary, Description;
        public bool Remote, Sponsor;
        public DateTime? Posted;
        public List<string> Skills = [];

        public FetchedJob ToFetched()
        {
            var (min, max) = Pay(Salary);
            var notes = new List<string>();
            if (Sponsor) notes.Add("Willing to sponsor work authorization (H1B).");
            if (EmployerType is { } et) notes.Add($"Posted by: {et}.");
            var description = string.Join("\n\n", new[] { Description, notes.Count > 0 ? string.Join(" ", notes) : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return new FetchedJob(Guid, Cut(Title, 300), Cut(Company, 200), Cut(Location, 200), Remote, EmploymentType is null ? null : Cut(EmploymentType, 40),
                "Dice", ApplyUrl, [new ApplyOption("Dice", ApplyUrl, EmployerType == "Direct Hire")],
                description.Length == 0 ? null : Cut(description, 20000), min, max, Posted,
                string.Join("\n", [Title, .. Skills, Description ?? ""]));
        }
    }

    private static DiceJob? Parse(JsonNode j)
    {
        var guid = S(j["guid"]);
        var title = S(j["title"]);
        var url = S(j["detailsPageUrl"]);
        if (guid is null || guid.Length > 200 || title is null || !SafeUrl(url)) return null;
        var workplace = ((j["workplaceTypes"] as JsonArray) ?? []).Select(S).OfType<string>().ToList();
        var location = S(j["jobLocation"]?["displayName"]) ?? "";
        location = Regex.Replace(location, @",\s*(USA|United States)$", "", RegexOptions.IgnoreCase);
        return new DiceJob
        {
            Guid = guid, Title = title, Company = S(j["companyName"]) ?? "Confidential", ApplyUrl = url!,
            Remote = B(j["isRemote"]) || workplace.Contains("Remote", StringComparer.OrdinalIgnoreCase),
            Location = location.Length > 0 ? location : workplace.Contains("Remote", StringComparer.OrdinalIgnoreCase) ? "Remote" : "",
            EmploymentType = S(j["employmentType"]), EmployerType = S(j["employerType"]), Salary = S(j["salary"]),
            Sponsor = B(j["willingToSponsor"]), Description = S(j["summary"]),
            Posted = S(j["postedDate"]) is { } p && DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null,
        };
    }

    /// <summary>One MCP tool call. The server answers as JSON or as a server-sent event stream; both are handled.</summary>
    private async Task<JsonNode?> CallAsync(string tool, JsonObject args, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, options.Value.DiceMcpUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage res;
        try { res = await factory.CreateClient("dice").SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AppException(504, "JOBFEED_TIMEOUT", "Dice search timed out", "Dice took too long to answer. Try again in a minute.");
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Dice MCP server unreachable");
            throw new AppException(503, "JOBFEED_UNAVAILABLE", "Dice unavailable", "Couldn't reach Dice. Check the server's internet connection and try again.");
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if ((int)res.StatusCode == 429)
                throw new AppException(429, "JOBFEED_PROVIDER_LIMIT", "Dice is busy", "Dice is limiting searches right now. Try again in a few minutes.");
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Dice MCP {Tool} {Status}: {Body}", tool, (int)res.StatusCode, Cut(text, 300));
                throw new AppException(502, "JOBFEED_ERROR", "Dice search failed", "Dice returned an error. Try again in a minute.");
            }
            // Server-sent events: the JSON-RPC response is on a "data:" line.
            var json = res.Content.Headers.ContentType?.MediaType == "text/event-stream"
                ? string.Join("", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()))
                : text;
            JsonNode? rpc;
            try { rpc = JsonNode.Parse(json); }
            catch (System.Text.Json.JsonException) { throw Unreadable(tool, text); }
            if (rpc?["error"] is { } err)
            {
                log.LogWarning("Dice MCP {Tool} error: {Error}", tool, Cut(err.ToJsonString(), 300));
                throw new AppException(502, "JOBFEED_ERROR", "Dice search failed", "Dice rejected the search. Try different keywords.");
            }
            var result = rpc?["result"];
            var content = (result?["content"] as JsonArray)?.FirstOrDefault(c => S(c?["type"]) == "text");
            var payload = S(content?["text"]);
            if (result?["isError"]?.GetValue<bool>() == true)
            {
                log.LogWarning("Dice MCP {Tool} tool error: {Error}", tool, Cut(payload ?? "", 300));
                throw new AppException(502, "JOBFEED_ERROR", "Dice search failed", "Dice couldn't run that search. Try different keywords.");
            }
            // Tools return their data as a JSON document inside a text block (newer servers also send structuredContent).
            if (result?["structuredContent"] is JsonObject sc) return sc;
            try { return payload is null ? null : JsonNode.Parse(payload); }
            catch (System.Text.Json.JsonException) { throw Unreadable(tool, payload ?? ""); }
        }
    }

    private AppException Unreadable(string tool, string text)
    {
        log.LogWarning("Dice MCP {Tool} returned something unreadable: {Body}", tool, Cut(text, 300));
        return new AppException(502, "JOBFEED_ERROR", "Dice search failed", "Dice returned something unreadable. Try again in a minute.");
    }

    /// <summary>"31 - 36", "USD 60.00 - 70.00 per hour", "USD 197,300.00 - 225,100.00 per year", "Depends on Experience" → $/hr.</summary>
    internal static (decimal? Min, decimal? Max) Pay(string? salary)
    {
        if (string.IsNullOrWhiteSpace(salary)) return (null, null);
        var nums = Regex.Matches(salary, @"\d[\d,]*(?:\.\d+)?")
            .Select(m => decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0).Where(v => v > 0).ToList();
        if (nums.Count == 0) return (null, null);
        var s = salary.ToLowerInvariant();
        var period = Regex.IsMatch(s, @"year|annual|annum|\byr\b|/yr") ? "YEAR"
            : Regex.IsMatch(s, @"hour|\bhr\b|/hr") ? "HOUR"
            : s.Contains("month") ? "MONTH" : s.Contains("week") ? "WEEK" : s.Contains("day") ? "DAY"
            : nums.Max() <= 300 ? "HOUR" : nums.Min() >= 20000 ? "YEAR" : null;
        if (period is null) return (null, null);
        return (JSearchClient.Hourly(nums.Min(), period), JSearchClient.Hourly(nums.Max(), period));
    }

    /// <summary>Dice descriptions are HTML: keep paragraphs and list items as lines, drop tags and styling.</summary>
    private static string? Text(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var t = Regex.Replace(html, @"<\s*(br|/p|/li|/div|/h\d|/tr)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<\s*li\b[^>]*>", "• ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<[^>]+>", "");
        t = WebUtility.HtmlDecode(t).Replace(' ', ' ');
        t = Regex.Replace(t, @"[ \t]+", " ");
        t = Regex.Replace(t, @"\s*\n\s*(\n\s*)+", "\n\n").Trim();
        return t.Length == 0 ? null : t;
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static bool B(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
    private static bool SafeUrl(string? url) =>
        url is { Length: <= 2000 } && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
}
