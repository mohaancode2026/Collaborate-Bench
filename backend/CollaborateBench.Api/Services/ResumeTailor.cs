using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Ai;
using CollaborateBench.Api.Models;

namespace CollaborateBench.Api.Services;

/// <summary>
/// Tailors a consultant's resume to a job: Claude rewrites and reorders it, then <see cref="Verify"/> checks the result
/// against the original resume and strips or flags anything it can't support. Without Claude, a built-in version leads
/// with the matching skills and keeps the experience exactly as written.
/// <para>
/// Truthfulness is the core rule. These resumes go to vendors and clients; an invented skill, employer, date or number
/// is resume fraud and exposes the company. The AI may only reword, reorder and emphasise what the original says.
/// Missing skills are reported as gaps for the recruiter, never added.
/// </para>
/// </summary>
public sealed partial class ResumeTailor(ClaudeClient claude, ILogger<ResumeTailor> log)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>What the resume is being tailored for.</summary>
    public sealed record Target(string Title, string? Client, IReadOnlyList<string> Skills, string? Description, string? Location, string? Mode);

    public sealed record Result(ResumeDoc Doc, List<string> Changes, List<string> Gaps, List<string> Matched, List<string> Warnings, string Engine, string? Notice);

    private const string System = """
        You tailor IT consultants' resumes for specific contract roles in US IT staffing (bench sales). Recruiters send these
        resumes to vendors and clients, so they must be truthful: an invented detail is resume fraud.

        Hard rules:
        1. Use only facts stated in the ORIGINAL RESUME and the PROFILE. Never add employers, job titles, dates, degrees, schools,
           certifications, metrics or numbers, projects, end clients, industries, skills, tools or technologies the original does not state.
        2. You may: rewrite the summary to lead with the experience most relevant to the job; group skills and put the job's matching
           skills first; reorder bullets within each role so the relevant ones come first; tighten and rephrase bullets in clear, active
           language, using the job's wording only where the original describes the same thing; drop clearly irrelevant bullets from
           older roles (keep at least two bullets per role).
        3. Keep every role from the original with its company, title and dates exactly as written, newest first. Do not raise the
           consultant's title or seniority.
        4. A skill the job asks for that the original doesn't show goes in "gaps" only. Never add it to the resume or hint at it.
        5. Never include visa or work authorization, pay rate, age, photos or personal details.
        6. Resume style: no "I", US English, concise.
        """;

    /// <param name="consultant">Profile facts (name, title, skills, experience) the recruiter has on file.</param>
    /// <param name="source">The original resume text: the only source of facts.</param>
    public async Task<Result> TailorAsync(ConsultantDto consultant, string source, Target target, bool hideContact, string? focus, CancellationToken ct)
    {
        var evidence = new Facts(source, consultant);
        var required = (target.Skills.Count > 0 ? target.Skills : Rules.FindSkills($"{target.Title}\n{target.Description}"))
            .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var matched = required.Where(s => Mentions(evidence, s)).ToList();
        var gaps = required.Where(s => !Mentions(evidence, s)).ToList();

        var (doc, changes, engine, notice) = await TryClaudeAsync(consultant, source, target, required, focus, ct) is { } ai
            ? (ai.Doc, ai.Changes, "claude", (string?)null)
            : Fallback(consultant, source, matched, claude.Notice);

        // Name and contact always come from the profile on file, never from the model.
        doc.Name = consultant.Name;
        doc.Email = hideContact ? null : NullIfEmpty(consultant.Email);
        doc.Phone = hideContact ? null : NullIfEmpty(consultant.Phone);
        doc.Location = NullIfEmpty(consultant.Location) ?? NullIfEmpty(doc.Location);

        var warnings = Verify(doc, source, consultant, strip: true);
        return new Result(doc, changes, gaps, matched, warnings, engine, notice);
    }

    // ---------- Claude ----------

    private async Task<(ResumeDoc Doc, List<string> Changes)?> TryClaudeAsync(ConsultantDto c, string source, Target t, List<string> required,
        string? focus, CancellationToken ct)
    {
        var user = $$""""
            Tailor this consultant's resume for the job below. Follow the hard rules.

            JOB
            Title: {{t.Title}}
            Client: {{t.Client ?? "(not given)"}}
            Location / work mode: {{t.Location ?? "-"}} / {{t.Mode ?? "-"}}
            Required skills: {{(required.Count > 0 ? string.Join(", ", required) : "(see description)")}}
            Description:
            """{{t.Description ?? "(none)"}}"""
            {{(string.IsNullOrWhiteSpace(focus) ? "" : $"Recruiter's emphasis (only where the original supports it): {focus}\n")}}
            PROFILE (facts on file)
            Title: {{c.Title}} | Experience: {{c.Exp}} years | Skills: {{string.Join(", ", c.Skills)}}

            ORIGINAL RESUME
            """{{source}}"""

            Return JSON with exactly these keys:
            {
              "resume": {
                "headline": "the consultant's real title, optionally with their top relevant skills",
                "summary": "3-5 sentences",
                "skills": [ { "category": "e.g. Languages, Frameworks, Cloud & DevOps, Databases", "items": ["..."] } ],
                "experience": [ { "company": "", "title": "", "location": "", "start": "", "end": "", "bullets": ["..."], "environment": "tools listed for this role in the original, or empty" } ],
                "education": [ { "degree": "", "school": "", "year": "" } ],
                "certifications": ["only those in the original"]
              },
              "changes": ["each change you made, in one short line, e.g. 'Moved Kafka streaming work to the top of the Acme Bank role'"],
              "gaps": ["required skills the original does not show"]
            }
            """";

        var j = await claude.JsonAsync("ResumeTailor", System, user, 16000, ct, fast: false);
        if (j?["resume"] is not JsonObject r) return null;
        try
        {
            var doc = r.Deserialize<ResumeDoc>(WebJson) ?? new ResumeDoc();
            Clean(doc);
            if (doc.Experience.Count == 0 && string.IsNullOrWhiteSpace(doc.Summary)) return null;
            var changes = (j["changes"] as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Take(20).ToList();
            return (doc, changes);
        }
        catch (JsonException ex)
        {
            // Wrong shape from the model: use the built-in version rather than fail the request.
            log.LogWarning(ex, "Claude resume JSON had an unexpected shape");
            return null;
        }
    }

    // ---------- Built-in fallback ----------

    private static (ResumeDoc, List<string>, string, string?) Fallback(ConsultantDto c, string source, List<string> matched, string? claudeNotice)
    {
        var all = Rules.FindSkills(source).Concat(c.Skills).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var others = all.Where(s => !matched.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        var years = c.Exp > 0 ? $" with {c.Exp}+ years of experience" : "";
        var lead = matched.Count > 0 ? $"{(years.Length > 0 ? ", including" : " with")} hands-on work in {string.Join(", ", matched.Take(6))}" : "";
        var doc = new ResumeDoc
        {
            Headline = c.Title,
            Summary = $"{c.Title}{years}{lead}. {c.Summary}".Trim(),
            Skills = [.. new[] { new SkillGroup { Category = "Relevant to this role", Items = matched }, new SkillGroup { Category = "Other skills", Items = others } }
                .Where(g => g.Items.Count > 0)],
            AdditionalText = source.Trim(),
        };
        var changes = new List<string> { "Summary rewritten to lead with the skills this job asks for", "Skills regrouped: job-relevant skills first", "Experience kept exactly as in the original" };
        var notice = (claudeNotice ?? "The built-in engine was used.") + " It tailors the summary and skills only. Add an Anthropic API key for full tailoring of the experience section.";
        return (doc, changes, "rules", notice);
    }

    // ---------- Verification against the original ----------

    /// <summary>
    /// Checks the document against the original resume and profile. Unsupported skills and certifications are removed when
    /// <paramref name="strip"/> is true; everything else that can't be verified (employers, years, numbers, tools in bullets) is
    /// returned as a warning for the recruiter to check. Also used when a recruiter saves their own edits.
    /// </summary>
    public static List<string> Verify(ResumeDoc doc, string source, ConsultantDto c, bool strip)
    {
        var evidence = new Facts(source, c);
        var flat = evidence.Flat;
        var warnings = new List<string>();
        void Warn(string w) { if (!warnings.Contains(w)) warnings.Add(w); }

        foreach (var g in doc.Skills)
        {
            foreach (var item in g.Items.ToList())
            {
                if (Mentions(evidence, item)) continue;
                var canonical = Rules.Skills.Contains(item, StringComparer.OrdinalIgnoreCase);
                if (canonical && strip) { g.Items.Remove(item); Warn($"Removed \"{item}\" from skills: it isn't in the original resume."); }
                else Warn($"Skill \"{item}\" isn't in the original resume. Check it before sending.");
            }
        }
        doc.Skills.RemoveAll(g => g.Items.Count == 0);

        foreach (var job in doc.Experience)
        {
            if (job.Company.Length > 0 && !flat.Contains(Flat(job.Company)))
                Warn($"Employer \"{job.Company}\" doesn't appear in the original resume. Check the name.");
            foreach (var y in Years().Matches($"{job.Start} {job.End}").Select(m => m.Value).Distinct())
                if (!evidence.Contains(y)) Warn($"Year {y} for {Label(job)} isn't in the original resume. Check the dates.");
            foreach (var b in job.Bullets.Append(job.Environment ?? ""))
            {
                foreach (var s in Rules.FindSkills(b).Where(s => !Mentions(evidence, s)))
                    Warn($"{Label(job)} mentions {s}, which isn't in the original resume. Remove it or confirm with the consultant.");
                foreach (var n in Metric().Matches(b).Select(m => m.Groups[1].Value).Distinct().Where(n => !evidence.Contains(n)))
                    Warn($"{Label(job)} mentions the number {n}, which isn't in the original resume. Check it.");
            }
        }

        foreach (var s in Rules.FindSkills($"{doc.Headline} {doc.Summary}").Where(s => !Mentions(evidence, s)))
            Warn($"The headline or summary mentions {s}, which isn't in the original resume.");
        foreach (var n in Metric().Matches(doc.Summary).Select(m => m.Groups[1].Value).Distinct().Where(n => !evidence.Contains(n)))
            Warn($"The summary mentions the number {n}, which isn't in the original resume. Check it.");

        foreach (var cert in doc.Certifications.ToList())
        {
            var words = Regex.Split(cert, @"\W+").Where(w => w.Length >= 4).ToList();
            var supported = flat.Contains(Flat(cert)) || (words.Count > 0 && words.All(w => flat.Contains(Flat(w))));
            if (supported) continue;
            if (strip) { doc.Certifications.Remove(cert); Warn($"Removed certification \"{cert}\": it isn't in the original resume."); }
            else Warn($"Certification \"{cert}\" isn't in the original resume. Check it before sending.");
        }
        foreach (var ed in doc.Education.Where(e => !string.IsNullOrWhiteSpace(e.School)))
        {
            var longest = Regex.Split(ed.School!, @"\W+").OrderByDescending(w => w.Length).FirstOrDefault() ?? "";
            if (longest.Length >= 4 && !flat.Contains(Flat(longest))) Warn($"School \"{ed.School}\" isn't in the original resume. Check it.");
        }
        return warnings;
    }

    /// <summary>Trims everything and drops empty entries so the editor and the Word file stay tidy.</summary>
    public static void Clean(ResumeDoc d)
    {
        d.Headline = d.Headline.Trim(); d.Summary = d.Summary.Trim();
        foreach (var g in d.Skills) g.Items = g.Items.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        d.Skills.RemoveAll(g => g.Items.Count == 0);
        foreach (var j in d.Experience) j.Bullets = j.Bullets.Select(b => b.Trim().TrimStart('•', '-', '*', ' ')).Where(b => b.Length > 0).ToList();
        d.Experience.RemoveAll(j => j.Company.Trim().Length == 0 && j.Title.Trim().Length == 0 && j.Bullets.Count == 0);
        d.Education.RemoveAll(e => string.IsNullOrWhiteSpace(e.Degree) && string.IsNullOrWhiteSpace(e.School));
        d.Certifications = d.Certifications.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
    }

    /// <summary>The facts we accept, indexed once: the resume text, the profile skills and the years of experience on file.</summary>
    private sealed class Facts
    {
        public readonly string Text;
        public readonly string Flat;
        public readonly HashSet<string> Skills;
        public Facts(string source, ConsultantDto c)
        {
            Text = $"{source}\n{string.Join(", ", c.Skills)}\n{c.Exp}\n{c.Title}";
            Flat = ResumeTailor.Flat(Text);
            Skills = new(Rules.FindSkills(Text), StringComparer.OrdinalIgnoreCase);
        }
        public bool Contains(string s) => Text.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    private static string Flat(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");

    /// <summary>
    /// Known skills must match exactly (so "JavaScript" in the resume is not evidence of "Java");
    /// other terms are accepted if the same letters and digits appear anywhere in the facts.
    /// </summary>
    private static bool Mentions(Facts f, string term)
    {
        var canonical = Rules.FindSkills(term);
        return canonical.Count > 0 ? canonical.All(f.Skills.Contains) : f.Flat.Contains(Flat(term));
    }
    private static string Label(ResumeJob j) => string.IsNullOrWhiteSpace(j.Company) ? "A role" : $"The {j.Company} role";
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex("[^a-z0-9]")] private static partial Regex NonAlnum();
    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")] private static partial Regex Years();
    // Numbers presented as results: 40%, 3x, 10+, $2M, 500K, 1.5 million.
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:%|\+|x\b|k\b|m\b|million|billion|users|tps|requests)", RegexOptions.IgnoreCase)] private static partial Regex Metric();
}
