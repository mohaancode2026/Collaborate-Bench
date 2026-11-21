using System.Text.Json;
using System.Text.Json.Nodes;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;

namespace CollaborateBench.Api.Ai;

/// <summary>All AI features. Each uses Claude when configured and falls back to <see cref="Rules"/> otherwise.</summary>
public sealed class AiService(ClaudeClient claude, DataService data, ICurrentUser me, ILogger<AiService> log)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private const string Persona = "You are an expert assistant inside Collaborate-Bench, a US IT bench sales (C2C/W2 staffing) platform. Be accurate, concise and professional.";

    // ---------- Resume parsing ----------
    public async Task<ParsedResume> ParseResumeAsync(string text, CancellationToken ct)
    {
        var rules = Rules.ParseResume(text);
        var j = await claude.JsonAsync("ResumeParse", Persona,
            $$""""
            Extract these fields from the resume. JSON keys: name, title, email, phone, location ("City, ST" or "Remote"),
            visa (one of {{string.Join(", ", Domain.Visas)}} or ""), exp (total years, number), rate (hourly USD if stated, else null),
            skills (array of up to 14 canonical technical skills), summary (1-2 sentence marketable hotlist summary), relocate (boolean).
            RESUME:
            """{{(text.Length > 14000 ? text[..14000] : text)}}"""
            """", 1200, ct);
        if (j is null) { rules.Notice = claude.Notice; return rules; }
        try
        {
            string S(string k, string fallback) => j[k]?.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(j[k]!.GetValue<string>()) ? j[k]!.GetValue<string>() : fallback;
            var skills = j["skills"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : rules.Skills;
            var visa = S("visa", rules.Visa);
            return new ParsedResume
            {
                Name = S("name", rules.Name), Title = S("title", rules.Title), Email = S("email", rules.Email), Phone = S("phone", rules.Phone),
                Location = S("location", rules.Location), Visa = Domain.Visas.Contains(visa) ? visa : rules.Visa,
                Exp = j["exp"]?.GetValueKind() == JsonValueKind.Number ? (int)j["exp"]!.GetValue<double>() : rules.Exp,
                Rate = j["rate"]?.GetValueKind() == JsonValueKind.Number ? (decimal)j["rate"]!.GetValue<double>() : rules.Rate,
                Skills = skills.Count > 0 ? skills : rules.Skills, Summary = S("summary", rules.Summary),
                Relocate = j["relocate"]?.GetValueKind() == JsonValueKind.True || rules.Relocate, Source = "claude",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            log.LogWarning(ex, "Claude resume JSON had unexpected types");
            rules.Notice = "Claude's answer had an unexpected format, so the built-in engine was used.";
            return rules;
        }
    }

    // ---------- Requirement extraction (sourcing agent) ----------
    public async Task<ExtractedRequirement> ExtractRequirementAsync(string text, CancellationToken ct)
    {
        var rules = Rules.ExtractRequirement(text);
        var j = await claude.JsonAsync("ReqExtract", Persona,
            $$""""
            Extract the contract requirement from this vendor email. JSON keys: title, client (end client), vendor (who sent it),
            tier (Direct|Prime vendor|Implementation partner|Tier-1|Tier-2), location, mode (Remote|Hybrid|Onsite), type (C2C|W2|1099),
            rate (number or null), skills (array of canonical skills), visas (array from {{string.Join(", ", Domain.Visas)}}, or ["Any"]).
            EMAIL:
            """{{(text.Length > 8000 ? text[..8000] : text)}}"""
            """", 900, ct);
        if (j is null) return new(rules, "rules", claude.Notice);
        string S(string k, string fb, string[]? allowed = null)
        {
            var v = j[k]?.GetValueKind() == JsonValueKind.String ? j[k]!.GetValue<string>() : null;
            return string.IsNullOrWhiteSpace(v) || (allowed != null && !allowed.Contains(v)) ? fb : v;
        }
        List<string> L(string k, List<string>? fb) => j[k] is JsonArray a && a.Count > 0 ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : fb ?? new();
        var req = rules with
        {
            Title = S("title", rules.Title ?? ""), Client = S("client", rules.Client ?? ""), Vendor = S("vendor", rules.Vendor ?? ""),
            Tier = S("tier", rules.Tier ?? "Tier-1", Domain.Tiers), Location = S("location", rules.Location ?? ""),
            Mode = S("mode", rules.Mode ?? "Hybrid", Domain.WorkModes), Type = S("type", rules.Type ?? "C2C", Domain.ContractTypes),
            Rate = j["rate"]?.GetValueKind() == JsonValueKind.Number ? (decimal)j["rate"]!.GetValue<double>() : rules.Rate,
            Skills = L("skills", rules.Skills), Visas = L("visas", rules.Visas),
        };
        return new(req, "claude");
    }

    // ---------- Job description (posting agent) ----------
    public async Task<AiText> JobDescriptionAsync(JdInput j, CancellationToken ct)
    {
        var text = await claude.TextAsync("JdWrite", Persona,
            $"""
            Write a professional contract job description in plain text (use • for bullets, no markdown headings symbols), 220-320 words.
            Title: {j.Title}; Client: {j.Client}; Vendor: {j.Vendor} ({j.Tier}); Location: {j.Location} ({j.Mode}); Contract: {j.Type};
            Bill rate: ${j.Rate:0}/hr; Skills: {string.Join(", ", j.Skills ?? new())}; Visa: {string.Join(", ", j.Visas ?? ["Any"])}.
            Sections: header line, About the role, Key responsibilities, Required skills, Nice to have, How to apply (resume, visa, location, rate, RTR required).
            """, 900, false, ct);
        return text is null ? new(Rules.JobDescription(j), "rules", claude.Notice) : new(text, "claude");
    }

    // ---------- Submission pitch ----------
    public async Task<AiText> PitchAsync(ConsultantDto c, RequirementDto r, CancellationToken ct)
    {
        var m = Matching.Score(c, r);
        var text = await claude.TextAsync("MatchPitch", Persona,
            $"Write a short persuasive submission email (plain text, under 160 words) from bench sales recruiter {me.Name} to vendor {r.Vendor}, submitting " +
            $"{c.Name} ({c.Title}, {c.Exp}+ yrs, visa {c.Visa}, {c.Location}, skills {string.Join(", ", c.Skills)}) for {r.Title} at {r.Client} ({r.Location}, {r.Type}). " +
            $"Matched skills: {string.Join(", ", m.Hit)}. Gaps: {(m.Miss.Count > 0 ? string.Join(", ", m.Miss) : "none")}. Mention RTR attached and immediate availability. Do not mention rates.",
            600, true, ct);
        return text is null ? new(Rules.Pitch(c, r, m, me.Name), "rules", claude.Notice) : new(text, "claude");
    }

    // ---------- Rate advice ----------
    public async Task<RateAdvice> RateAdviceAsync(ConsultantDto c, RequirementDto r, CancellationToken ct)
    {
        var fallback = Matching.SuggestRate(c.Rate, r.Rate);
        var why = $"Keeps about 10–15% over the ${c.Rate:0}/hr pay rate and leaves the vendor room under the ${r.Rate:0}/hr bill rate.";
        var m = Matching.Score(c, r);
        var j = await claude.JsonAsync("RateAdvice", Persona,
            $"Suggest the hourly rate our firm should submit to the vendor. Consultant pay rate ${c.Rate}/hr ({c.Visa}, {c.Exp} yrs, match {m.Score}%). " +
            $"Vendor bill rate to us: ${r.Rate}/hr, {r.Type}, {r.Tier}. JSON: {{\"rate\": number, \"why\": \"one sentence\"}}. The rate must be above the pay rate and at most the bill rate.",
            300, ct);
        if (j?["rate"]?.GetValueKind() == JsonValueKind.Number)
        {
            var rate = Math.Round((decimal)j["rate"]!.GetValue<double>());
            if (rate > c.Rate && rate <= r.Rate)
                return new(rate, j["why"]?.ToString() ?? why, "claude");
        }
        return new(fallback, why, "rules", claude.Notice);
    }

    // ---------- Interview prep ----------
    public async Task<AiText> InterviewPrepAsync(SubmissionDto s, ConsultantDto c, RequirementDto r, CancellationToken ct)
    {
        var m = Matching.Score(c, r);
        var text = await claude.TextAsync("InterviewPrep", Persona,
            $"Create a concise interview prep sheet (plain text, under 320 words) for {c.Name} ({c.Title}, {c.Exp} yrs, skills: {string.Join(", ", c.Skills)}) " +
            $"at stage \"{s.Stage}\" for {r.Title} at {r.Client} via {r.Vendor}. Required skills: {string.Join(", ", r.Skills)}. Gaps: {string.Join(", ", m.Miss)}. " +
            "Sections: Likely technical questions (8, specific), Gaps to prepare for, 3 talking points, Logistics. Remind them not to discuss rate.",
            1000, false, ct);
        return text is null ? new(Rules.InterviewPrep(s, c, r, m), "rules", claude.Notice) : new(text, "claude");
    }

    // ---------- Daily briefing ----------
    public async Task<AiText> BriefingAsync(string? scope, CancellationToken ct)
    {
        var cons = await data.ConsultantsAsync(scope, ct);
        var subs = await data.SubmissionsAsync(scope, ct);
        var open = await data.RequirementsAsync(true, ct);
        var ins = Insights.Build(cons, subs, open, me.Has("margin.view"));
        var bench = cons.Count(c => c.Status is "Bench" or "Marketing");
        var active = subs.Count(s => s.Stage != "Billing");
        var facts = string.Join("\n", ins.Select(i => $"- {i.Title}: {i.Detail}"));
        var text = await claude.TextAsync("Briefing", Persona,
            $"Write a crisp morning briefing (max 120 words, 3-5 lines starting with •) for {me.Name} ({me.Role}). Prioritise today's actions and name people.\n" +
            $"Bench: {bench}, active submissions: {active}, open requirements: {open.Count}.\n{(facts.Length > 0 ? facts : "- No risks flagged")}",
            500, true, ct);
        return text is null ? new(Rules.Briefing(me.Name.Split(' ')[0], bench, active, open.Count, ins), "rules", claude.Notice) : new(text, "claude");
    }

    // ---------- Copilot (tool use; tools only return rows this user may see) ----------
    private static JsonObject Tool(string name, string description, JsonObject props) => new()
    {
        ["name"] = name, ["description"] = description,
        ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = props },
    };
    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };

    private static JsonArray Tools() => new(
        Tool("list_consultants", "Consultants the user may see: skills, visa, pay rate, owner, days on bench, active submission count.",
            new JsonObject { ["skill"] = Str("filter by skill"), ["status"] = Str("Bench | Marketing | Placed"), ["owner"] = Str("recruiter name") }),
        Tool("list_requirements", "Open job requirements (shared across teams).", new JsonObject { ["skill"] = Str("filter by skill or title") }),
        Tool("list_submissions", "Submissions in the user's scope with stage and days in stage.", new JsonObject { ["stage"] = Str("stage name") }),
        Tool("top_matches", "Best consultant-to-requirement matches not yet submitted, scored 0-100 with reasons.",
            new JsonObject { ["consultant"] = Str("consultant name"), ["requirement"] = Str("requirement title or client") }),
        Tool("team_summary", "Per-recruiter counts of consultants, active submissions and placements in the user's scope.", new JsonObject()));

    private async Task<object> RunToolAsync(string name, JsonNode? input, CancellationToken ct)
    {
        string? A(string k) => input?[k]?.ToString() is { Length: > 0 } v ? v : null;
        bool Has(IEnumerable<string> xs, string? q) => q is null || xs.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase));
        var cons = await data.ConsultantsAsync(null, ct);
        switch (name)
        {
            case "list_consultants":
                return cons.Where(c => Has(c.Skills, A("skill")) && (A("status") is null || c.Status.Equals(A("status"), StringComparison.OrdinalIgnoreCase)) && Has([c.OwnerName], A("owner")))
                    .Select(c => new { c.Name, c.Title, c.Visa, c.Location, payRate = c.Rate, c.Exp, c.Skills, c.Status, owner = c.OwnerName, c.DaysOnBench, c.ActiveSubmissions });
            case "list_requirements":
                return (await data.RequirementsAsync(true, ct)).Where(r => Has(r.Skills.Append(r.Title), A("skill")))
                    .Select(r => new { r.Title, r.Client, r.Vendor, r.Type, billRate = r.Rate, r.Location, r.Mode, r.Skills, r.Visas, r.DaysOld });
            case "list_submissions":
                return (await data.SubmissionsAsync(null, ct)).Where(s => A("stage") is null || s.Stage.Equals(A("stage"), StringComparison.OrdinalIgnoreCase))
                    .Select(s => new { consultant = s.ConsultantName, requirement = s.Title, s.Client, s.Stage, s.DaysInStage, owner = s.OwnerName, s.MarginPerHour });
            case "top_matches":
                var subs = (await data.SubmissionsAsync(null, ct)).Select(s => (s.ConsultantId, s.RequirementId)).ToHashSet();
                var open = await data.RequirementsAsync(true, ct);
                return cons.Where(c => c.Status is "Bench" or "Marketing" && Has([c.Name], A("consultant")))
                    .SelectMany(c => open.Where(r => !subs.Contains((c.Id, r.Id)) && Has([r.Title, r.Client], A("requirement"))).Select(r => Matching.Score(c, r)))
                    .OrderByDescending(m => m.Score).Take(8)
                    .Select(m => new { consultant = m.ConsultantName, requirement = m.RequirementTitle, m.Client, m.Score, m.Why });
            case "team_summary":
                var all = await data.SubmissionsAsync(null, ct);
                return cons.GroupBy(c => c.OwnerName).Select(g => new
                {
                    recruiter = g.Key, consultants = g.Count(),
                    activeSubmissions = all.Count(s => s.OwnerName == g.Key && s.Stage != "Billing"),
                    placed = all.Count(s => s.OwnerName == g.Key && s.Stage == "Billing"),
                });
            default:
                return new { error = $"Unknown tool {name}" };
        }
    }

    public async Task<AiText> CopilotAsync(List<ChatMessage> history, CancellationToken ct)
    {
        var turns = history.Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Role is "user" or "assistant").TakeLast(10).ToList();
        if (turns.Count == 0 || turns[^1].Role != "user")
            throw AppException.Bad("VALIDATION", "Ask a question", "Type a question for the copilot.");
        var question = turns[^1].Content!;
        if (question.Length > 1000) throw AppException.Bad("VALIDATION", "Message too long", "Keep questions under 1,000 characters.");
        while (turns.Count > 0 && turns[0].Role != "user") turns.RemoveAt(0);

        if (claude.Enabled)
        {
            var system = $"{Persona} Signed-in user: {me.Name}, role {me.Role}. Today is {DateTime.UtcNow:yyyy-MM-dd}. " +
                         "Use the tools to look up data. The tools already limit rows to what this user is allowed to see; never guess about other teams. " +
                         (me.Has("margin.view") ? "" : "This user may not see margins; never estimate them. ") +
                         "Answer in short plain-text bullets with names and numbers, then one suggested next action. You cannot change data; point to the right page (Bench, Match Agent, Submissions, Onboarding).";
            var messages = new JsonArray(turns.Select(t => (JsonNode?)new JsonObject { ["role"] = t.Role, ["content"] = t.Content }).ToArray());
            for (var step = 0; step < 6; step++)
            {
                var res = await claude.SendAsync("Copilot", new JsonObject
                {
                    ["model"] = claude.Model(false), ["max_tokens"] = 1200, ["system"] = system, ["tools"] = Tools(), ["messages"] = messages.DeepClone(),
                }, ct);
                if (res is null) break;
                if (res["stop_reason"]?.GetValue<string>() != "tool_use")
                    return new(ClaudeClient.TextOf(res), "claude");

                var content = res["content"]!.AsArray();
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
                var results = new JsonArray();
                foreach (var block in content.Where(b => b?["type"]?.GetValue<string>() == "tool_use"))
                {
                    object output;
                    try { output = await RunToolAsync(block!["name"]!.GetValue<string>(), block["input"], ct); }
                    catch (AppException ex) { output = new { error = ex.Message }; }
                    results.Add(new JsonObject
                    {
                        ["type"] = "tool_result", ["tool_use_id"] = block!["id"]!.GetValue<string>(),
                        ["content"] = JsonSerializer.Serialize(output, WebJson),
                    });
                }
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
            }
        }
        return new(await LocalCopilotAsync(question, ct), "rules", claude.Notice);
    }

    private async Task<string> LocalCopilotAsync(string q, CancellationToken ct)
    {
        var t = q.ToLowerInvariant();
        var cons = await data.ConsultantsAsync(null, ct);
        var subs = await data.SubmissionsAsync(null, ct);
        var open = await data.RequirementsAsync(true, ct);
        string Lines<T>(IEnumerable<T> xs, Func<T, string> f) => string.Join("\n", xs.Select(x => "• " + f(x)));

        if (System.Text.RegularExpressions.Regex.IsMatch(t, "idle|no (active )?sub|not submitted|sitting"))
        {
            var idle = cons.Where(c => c.Status is "Bench" or "Marketing" && c.ActiveSubmissions == 0).OrderByDescending(c => c.DaysOnBench).ToList();
            return idle.Count == 0 ? "Everyone on your bench has at least one active submission." : "No active submission:\n" + Lines(idle, c => $"{c.Name} ({c.Title}), {c.DaysOnBench}d on bench, owner {c.OwnerName}");
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(t, "stuck|stale|delay|follow"))
        {
            var stuck = subs.Where(s => s.Stage != "Billing" && s.DaysInStage >= 3).ToList();
            return stuck.Count == 0 ? "No stuck submissions right now." : "Waiting 3+ days:\n" + Lines(stuck, s => $"{s.ConsultantName} → {s.Title} ({s.Client}): {s.Stage} for {s.DaysInStage}d, {s.OwnerName}") + "\nNext step: chase the vendors for feedback today.";
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(t, "match|best|fit|submit"))
        {
            var done = subs.Select(s => (s.ConsultantId, s.RequirementId)).ToHashSet();
            var top = cons.Where(c => c.Status is "Bench" or "Marketing").SelectMany(c => open.Where(r => !done.Contains((c.Id, r.Id))).Select(r => Matching.Score(c, r)))
                .OrderByDescending(m => m.Score).Take(5).ToList();
            return top.Count == 0 ? "No unsubmitted matches found." : "Top matches to submit:\n" + Lines(top, m => $"{m.ConsultantName} → {m.RequirementTitle} ({m.Client}): {m.Score}%");
        }
        if (t.Contains("team") || t.Contains("recruiter") || t.Contains("performance"))
            return "Team snapshot:\n" + Lines(cons.GroupBy(c => c.OwnerName), g => $"{g.Key}: {g.Count()} consultants, {subs.Count(s => s.OwnerName == g.Key && s.Stage != "Billing")} active, {subs.Count(s => s.OwnerName == g.Key && s.Stage == "Billing")} placed");
        var skill = Rules.Skills.FirstOrDefault(s => t.Contains(s.ToLowerInvariant()));
        if (skill != null)
        {
            var cs = cons.Where(c => c.Skills.Contains(skill, StringComparer.OrdinalIgnoreCase)).Select(c => c.Name).ToList();
            var rs = open.Where(r => r.Skills.Contains(skill, StringComparer.OrdinalIgnoreCase)).Select(r => $"{r.Title} @ {r.Client}").ToList();
            return $"{skill}: {cs.Count} consultant(s) ({(cs.Count > 0 ? string.Join(", ", cs) : "none")}) and {rs.Count} open requirement(s) ({(rs.Count > 0 ? string.Join("; ", rs) : "none")}).";
        }
        return $"Snapshot: {cons.Count(c => c.Status != "Placed")} on bench, {subs.Count(s => s.Stage != "Billing")} active submissions, {open.Count} open requirements.\n\n" +
               "Try: “Who has no active submission?”, “Which submissions are stuck?”, “Best matches for Java”, “How is my team doing?”";
    }
}
