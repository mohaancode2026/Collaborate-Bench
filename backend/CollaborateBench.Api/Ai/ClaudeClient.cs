using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CollaborateBench.Api.Data;
using Microsoft.EntityFrameworkCore;
using CollaborateBench.Api.Security;

namespace CollaborateBench.Api.Ai;

/// <summary>
/// Calls the Anthropic Messages API from the server (the API key never reaches the browser).
/// Returns null on any failure so callers can fall back to the built-in engine, and sets
/// <see cref="Notice"/> to a message the UI can show. Every call is logged in ai.Interactions.
/// </summary>
public sealed class ClaudeClient(IHttpClientFactory factory, IConfiguration cfg, IDbContextFactory<AppDbContext> dbFactory, ICurrentUser me, ILogger<ClaudeClient> log)
{
    private string ApiKey => cfg["Anthropic:ApiKey"] ?? "";
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
    public string Model(bool fast) => (fast ? cfg["Anthropic:FastModel"] : null) ?? cfg["Anthropic:Model"] ?? "claude-sonnet-5-5";
    public string? Notice { get; private set; }

    // Models that accept server-side refusal fallbacks ("fallbacks": "default"): if a safety classifier declines,
    // the API re-runs the request on a suitable model inside the same call instead of returning a refusal.
    private static readonly HashSet<string> FallbackModels = new(StringComparer.OrdinalIgnoreCase)
        { "claude-fable-5-2", "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5" };

    public async Task<JsonNode?> SendAsync(string feature, JsonObject body, CancellationToken ct)
    {
        if (!Enabled) { Notice = null; return null; }
        var model = body["model"]?.GetValue<string>() ?? Model(false);
        var sw = Stopwatch.StartNew();
        try
        {
            var http = factory.CreateClient("claude");
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
            req.Headers.Add("x-api-key", ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            if (FallbackModels.Contains(model) && body["fallbacks"] is null)
            {
                req.Headers.Add("anthropic-beta", "server-side-fallback-2026-07-01");
                body["fallbacks"] = "default";
            }
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var status = (int)res.StatusCode;
                var code = status switch { 401 or 403 => "AI_AUTH", 429 => "AI_RATE_LIMITED", 529 => "AI_OVERLOADED", >= 500 => "AI_SERVER", _ => "AI_BAD_REQUEST" };
                Notice = code is "AI_RATE_LIMITED" or "AI_OVERLOADED"
                    ? "Claude is busy right now, so the built-in engine answered instead. Try again in a minute."
                    : "Claude couldn't be reached, so the built-in engine answered instead.";
                if (code == "AI_AUTH") log.LogError("Anthropic API key rejected ({Status}). Check Anthropic:ApiKey.", status);
                else log.LogWarning("Anthropic API {Status}: {Body}", status, text.Length > 500 ? text[..500] : text);
                await TrackAsync(feature, model, null, sw, false, code);
                return null;
            }
            var node = JsonNode.Parse(text);
            // A 200 can still be a refusal (the whole fallback chain declined) or an answer cut off at max_tokens.
            // Neither is usable, so the caller falls back to the built-in engine.
            var stop = node?["stop_reason"]?.GetValue<string>();
            if (stop is "refusal" or "max_tokens")
            {
                Notice = stop == "refusal"
                    ? "Claude declined this request, so the built-in engine answered instead."
                    : "Claude's answer was too long and got cut off, so the built-in engine answered instead.";
                log.LogWarning("Anthropic API {Feature} stopped with {StopReason}", feature, stop);
                await TrackAsync(feature, model, node?["usage"], sw, false, stop == "refusal" ? "AI_REFUSAL" : "AI_MAX_TOKENS");
                return null;
            }
            Notice = null;
            await TrackAsync(feature, model, node?["usage"], sw, true, null);
            return node;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Notice = "Claude took too long to respond, so the built-in engine answered instead.";
            log.LogWarning(ex, "Anthropic API call failed for {Feature}", feature);
            await TrackAsync(feature, model, null, sw, false, "AI_TIMEOUT");
            return null;
        }
    }

    public static string TextOf(JsonNode? response) =>
        string.Concat((response?["content"]?.AsArray() ?? new JsonArray())
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b!["text"]!.GetValue<string>())).Trim();

    public async Task<string?> TextAsync(string feature, string system, string user, int maxTokens = 1200, bool fast = false, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = Model(fast), ["max_tokens"] = maxTokens, ["system"] = system,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        var res = await SendAsync(feature, body, ct);
        var text = res is null ? null : TextOf(res);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Asks for JSON only and parses the first JSON object in the reply.</summary>
    /// <param name="fast">True = the fast model (extraction); false = the main model for quality-sensitive writing.</param>
    public async Task<JsonObject?> JsonAsync(string feature, string system, string user, int maxTokens = 1200, CancellationToken ct = default, bool fast = true)
    {
        var text = await TextAsync(feature, system + "\nRespond with a single JSON object only. No prose, no code fences.", user, maxTokens, fast, ct);
        if (text is null) return null;
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) { Notice = "Claude's answer couldn't be read, so the built-in engine was used."; return null; }
        try { return JsonNode.Parse(text[start..(end + 1)]) as JsonObject; }
        catch (System.Text.Json.JsonException) { Notice = "Claude's answer couldn't be read, so the built-in engine was used."; return null; }
    }

    private async Task TrackAsync(string feature, string model, JsonNode? usage, Stopwatch sw, bool ok, string? error)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.AiInteractions.Add(new AiInteraction
            {
                UserId = me.Id, Feature = feature, Model = model,
                InputTokens = usage?["input_tokens"]?.GetValue<int>(), OutputTokens = usage?["output_tokens"]?.GetValue<int>(),
                LatencyMs = (int)sw.ElapsedMilliseconds, Succeeded = ok, ErrorCode = error,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not record AI usage"); }
    }
}
