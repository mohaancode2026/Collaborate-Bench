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

        Clean(doc);
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
        var sections = SplitSections(source);

        // Summary: one lead sentence built from profile facts, then the original summary in its own words (whole sentences only).
        var who = string.IsNullOrWhiteSpace(c.Title) ? "IT professional" : c.Title.Trim();
        var years = c.Exp > 0 ? $" with {c.Exp}+ years of experience" : "";
        var lead = matched.Count > 0 ? $"{(years.Length > 0 ? ", including" : " with")} hands-on work in {string.Join(", ", matched.Take(6))}" : "";
        var original = WholeSentences(Flatten(sections.GetValueOrDefault("summary")) ?? c.Summary, 900);
        var summary = $"{char.ToUpperInvariant(who[0])}{who[1..]}{years}{lead}.";
        if (original.Length > 0) summary += " " + original;

        // Experience: only the experience (and projects) part of the original, tidied. Falls back to everything that
        // isn't a recognised section, so nothing from the original is lost.
        var experience = sections.GetValueOrDefault("experience");
        if (sections.GetValueOrDefault("projects") is { } projects) experience = $"{experience}\n\nPROJECTS\n{projects}";
        experience = WithoutContactLines(experience ?? sections.GetValueOrDefault("other"), c.Name);

        var doc = new ResumeDoc
        {
            Headline = c.Title,
            Summary = summary.Trim(),
            Skills = [.. new[] { new SkillGroup { Category = "Relevant to this role", Items = matched }, new SkillGroup { Category = "Other skills", Items = others } }
                .Where(g => g.Items.Count > 0)],
            AdditionalText = TidyText(experience),
            Education = ParseEducation(sections.GetValueOrDefault("education")),
            Certifications = (TidyText(sections.GetValueOrDefault("certifications")) ?? "").Split('\n')
                .Select(l => l.Trim().TrimStart('•', '-', '*', '·', '▪', ' ')).Where(l => l.Length is > 2 and <= 200).Take(12).ToList(),
        };
        var changes = new List<string> { "Summary rewritten to lead with the skills this job asks for", "Skills regrouped: job-relevant skills first",
            "Experience kept exactly as in the original (blank lines tidied)" };
        if (doc.Education.Count > 0 || doc.Certifications.Count > 0) changes.Add("Education and certifications taken from the original into their own sections");
        var notice = (claudeNotice ?? "The built-in engine was used.") + " It tailors the summary and skills only. Add an Anthropic API key for full tailoring of the experience section.";
        return (doc, changes, "rules", notice);
    }

    // ---------- Reading the original's sections (built-in engine) ----------

    private static readonly (string Key, Regex Heading)[] SectionHeadings =
    [
        ("summary", new(@"^(professional|career|executive|personal)?\s*(summary|profile|objective|overview)|^about me$", RegexOptions.IgnoreCase)),
        ("skills", new(@"^(technical|key|core|professional)?\s*(skills?|competencies|expertise|skill set|technologies|tools)(\s+(summary|&\s*tools))?$", RegexOptions.IgnoreCase)),
        ("experience", new(@"^(professional|work|relevant|employment|career)?\s*(experience|history|employment)(\s+(summary|history|details))?$|^work history$", RegexOptions.IgnoreCase)),
        ("projects", new(@"^(key\s+|major\s+|academic\s+)?projects?$", RegexOptions.IgnoreCase)),
        ("education", new(@"^(education|academic\s+(qualifications?|background|details)|educational\s+qualifications?|qualifications)$", RegexOptions.IgnoreCase)),
        ("certifications", new(@"^(certifications?|certificates|licenses?\s*(&|and)\s*certifications?|certifications?\s*(&|and)\s*(training|courses))$", RegexOptions.IgnoreCase)),
    ];

    /// <summary>Splits resume text at its section headings ("EXPERIENCE", "Technical Skills:", ...). Text before the first
    /// heading (name and contacts) is dropped; text under an unknown heading, or with no headings at all, goes to "other".</summary>
    public static Dictionary<string, string> SplitSections(string source)
    {
        var result = new Dictionary<string, System.Text.StringBuilder>();
        string? current = null;
        var sawHeading = false;
        foreach (var raw in source.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var bare = line.TrimEnd(':').Trim();
            var key = bare.Length is > 2 and <= 45 ? SectionHeadings.FirstOrDefault(h => h.Heading.IsMatch(bare)).Key : null;
            if (key != null) { current = key; sawHeading = true; continue; }
            if (!sawHeading) { (result.TryGetValue("other", out var o) ? o : result["other"] = new()).AppendLine(line); continue; }
            var target = current ?? "other";
            (result.TryGetValue(target, out var sb) ? sb : result[target] = new()).AppendLine(line);
        }
        var map = result.ToDictionary(k => k.Key, v => v.Value.ToString().Trim());
        if (sawHeading) map.Remove("other");   // the header block (name, contacts) when the resume has real sections
        return map.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>Drops lines from the original text that carry contact details (email, phone, LinkedIn) or are just the
    /// candidate's name, e.g. a resume footer. Contact details only come from the profile, and only when not hidden.</summary>
    private static string? WithoutContactLines(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var keep = text.Replace("\r", "").Split('\n').Where(l =>
        {
            var t = l.Trim();
            if (t.Length == 0) return true;
            if (Regex.IsMatch(t, @"[\w.+-]+@[\w-]+\.[\w.]+")) return false;                                                   // email
            if (t.Length < 80 && Regex.IsMatch(t, @"(\+?\d{1,3}[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}")) return false;   // phone
            if (t.Length < 120 && Regex.IsMatch(t, @"linkedin\.com/", RegexOptions.IgnoreCase)) return false;
            if (name.Length > 0 && (t.Equals(name, StringComparison.OrdinalIgnoreCase)
                                    || (t.Length < name.Length + 20 && t.EndsWith(" " + name, StringComparison.OrdinalIgnoreCase)))) return false;
            return true;
        });
        return string.Join('\n', keep);
    }

    private static string? Flatten(string? s) => string.IsNullOrWhiteSpace(s) ? null : Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Cuts text to whole sentences within <paramref name="max"/> characters, so a summary never ends mid-word.</summary>
    private static string WholeSentences(string? s, int max)
    {
        s = Flatten(s) ?? "";
        if (s.Length == 0) return "";
        var sentences = Regex.Split(s, @"(?<=[.!?])\s+").Where(x => x.Length > 0).ToList();
        var complete = sentences.Where(x => Regex.IsMatch(x, @"[.!?]$")).ToList();
        var outText = "";
        foreach (var sent in complete) { if ((outText + " " + sent).Length > max) break; outText = (outText + " " + sent).Trim(); }
        return outText;
    }

    /// <summary>Education lines as entries: a line with a degree starts an entry; the next line without one is its school.
    /// The year is the last 4-digit year on either line.</summary>
    private static List<ResumeEducation> ParseEducation(string? text)
    {
        var lines = (TidyText(text) ?? "").Split('\n').Select(l => l.Trim().TrimStart('•', '-', '*', '·', '▪', ' ').Trim()).Where(l => l.Length > 1).ToList();
        var list = new List<ResumeEducation>();
        for (var i = 0; i < lines.Count && list.Count < 6; i++)
        {
            var line = lines[i].TrimEnd(':');
            if (!Rules.IsDegree(line) && list.Count > 0 && list[^1].School is null) { list[^1].School = line; continue; }
            var e = new ResumeEducation { Degree = line };
            if (i + 1 < lines.Count && !Rules.IsDegree(lines[i + 1])) { e.School = lines[i + 1].TrimEnd(':'); i++; }
            var year = Regex.Matches($"{e.Degree} {e.School}", @"\b(19|20)\d{2}\b").LastOrDefault()?.Value;
            if (year != null)
            {
                e.Year = year;
                e.Degree = Regex.Replace(e.Degree, @"[\s,–\-]*\b" + year + @"\b\s*$", "").Trim();
                if (e.School != null) e.School = Regex.Replace(e.School, @"[\s,–\-]*\b" + year + @"\b\s*$", "").Trim();
            }
            if (e.Degree.Length > 0) list.Add(e);
        }
        return list;
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
        // Design settings come from the browser: keep them to known values.
        d.Template = ResumeDoc.Templates.Contains(d.Template?.ToLowerInvariant()) ? d.Template!.ToLowerInvariant() : "classic";
        var accent = (d.Accent ?? "").TrimStart('#');
        d.Accent = Regex.IsMatch(accent, "^[0-9A-Fa-f]{6}$") ? accent.ToUpperInvariant() : "1E293B";
        d.HiddenSections = (d.HiddenSections ?? []).Select(s => s.ToLowerInvariant()).Where(ResumeDoc.Sections.Contains).Distinct().ToList();
        if (d.AdditionalText != null) d.AdditionalText = TidyText(d.AdditionalText);
    }

    /// <summary>Trims lines and collapses runs of blank lines (Word files turn every paragraph gap into empty lines).</summary>
    public static string? TidyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", "").Split('\n').Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim()).ToList();
        var sb = new System.Text.StringBuilder();
        var blank = false;
        foreach (var l in lines)
        {
            if (l.Length == 0) { blank = sb.Length > 0; continue; }
            if (blank) { sb.Append('\n'); blank = false; }
            sb.Append(l).Append('\n');
        }
        return sb.ToString().TrimEnd();
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
