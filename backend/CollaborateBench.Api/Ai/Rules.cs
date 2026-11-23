using System.Text;
using System.Text.RegularExpressions;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Services;

namespace CollaborateBench.Api.Ai;

/// <summary>Built-in engine used when Claude is not configured or unavailable. Deterministic and fast.</summary>
public static class Rules
{
    public static readonly string[] Skills =
    [
        "Java","Spring Boot","Hibernate","Python","Django","Flask","FastAPI","React","Angular","Vue","Node.js","TypeScript","JavaScript",".NET","C#","ASP.NET",
        "Go","Scala","Ruby","PHP","Kotlin","Swift","iOS","Android","AWS","Azure","GCP","Kubernetes","Docker","Terraform","Ansible","Jenkins","GitHub Actions",
        "CI/CD","DevOps","SRE","Linux","Microservices","REST","GraphQL","Kafka","RabbitMQ","Spark","PySpark","Hadoop","Databricks","Snowflake","Airflow","dbt",
        "ETL","Informatica","SQL","PL/SQL","Oracle","PostgreSQL","MySQL","SQL Server","MongoDB","Cassandra","Redis","DynamoDB","Elasticsearch","Tableau",
        "Power BI","Salesforce","Apex","LWC","SAP","SAP FICO","SAP ABAP","ServiceNow","Workday","Guidewire","Pega","Selenium","Cypress","Playwright",
        "Automation Testing","JMeter","Machine Learning","Deep Learning","TensorFlow","PyTorch","NLP","LLM","GenAI","Data Science","Pandas","Business Analyst",
        "Scrum","Agile","JIRA","Splunk","Datadog","Cybersecurity","Okta","Networking","Mainframe","COBOL","HTML","CSS","Redux","Next.js",
    ];

    public static List<string> FindSkills(string text)
    {
        var t = " " + Regex.Replace(text, @"\s+", " ") + " ";
        return Skills.Where(s => Regex.IsMatch(t, @"(^|[^A-Za-z0-9+#])" + Regex.Escape(s) + @"(?![A-Za-z0-9+#])", RegexOptions.IgnoreCase)).ToList();
    }

    public static ParsedResume ParseResume(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var p = new ParsedResume
        {
            Email = Regex.Match(text, @"[\w.+-]+@[\w-]+\.[\w.]+").Value,
            Phone = Regex.Match(text, @"(\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}").Value.Trim(),
            Skills = FindSkills(text),
            Relocate = Regex.IsMatch(text, "relocat", RegexOptions.IgnoreCase),
            Source = "rules",
        };
        p.Name = FindName(lines, p.Email);
        var tm = Regex.Match(text, @"((senior|sr\.?|lead|principal|staff|junior)\s+)?([A-Za-z.#+/ ]{2,30}?)\s(developer|engineer|architect|analyst|consultant|tester|administrator|scientist|manager|sdet)\b", RegexOptions.IgnoreCase);
        p.Title = tm.Success ? Regex.Replace(tm.Value.Trim(), @"\s+", " ") : (p.Skills.FirstOrDefault() is { } s ? s + " Consultant" : "");
        var ex = Regex.Match(text, @"(\d{1,2})\+?\s*(years|yrs)", RegexOptions.IgnoreCase);
        p.Exp = ex.Success ? int.Parse(ex.Groups[1].Value) : 0;
        var up = text.ToUpperInvariant();
        foreach (var (pattern, visa) in new[] { ("H4[- ]?EAD", "H4-EAD"), ("L2[- ]?EAD", "L2-EAD"), ("GC[- ]?EAD", "GC-EAD"), ("STEM OPT", "STEM OPT"), ("H-?1B", "H1B"),
                                                ("GREEN CARD", "GC"), ("\\bGC\\b", "GC"), ("US CITIZEN|\\bUSC\\b", "USC"), ("\\bOPT\\b", "OPT"), ("\\bCPT\\b", "CPT"), ("\\bTN\\b", "TN"), ("\\bEAD\\b", "EAD") })
            if (Regex.IsMatch(up, pattern)) { p.Visa = visa; break; }
        var loc = Regex.Match(text, @"\b([A-Z][a-zA-Z]+(?:\s[A-Z][a-zA-Z]+)?),\s?(A[LKZR]|C[AOT]|D[EC]|FL|GA|HI|I[ADLN]|K[SY]|LA|M[ADEINOST]|N[CDEHJMVY]|O[HKR]|PA|RI|S[CD]|T[NX]|UT|V[AT]|W[AIVY])\b");
        p.Location = loc.Success ? loc.Value : Regex.IsMatch(text, "remote", RegexOptions.IgnoreCase) ? "Remote" : "";
        var rate = Regex.Match(text, @"\$\s?(\d{2,3})(?:\.\d+)?\s*(?:/|per\s)?\s*(hr|hour|h)\b", RegexOptions.IgnoreCase);
        p.Rate = rate.Success ? decimal.Parse(rate.Groups[1].Value) : null;
        var si = lines.FindIndex(l => Regex.IsMatch(l, @"^(professional\s+)?(summary|profile|objective)", RegexOptions.IgnoreCase));
        var summary = si >= 0 && si + 1 < lines.Count ? lines[si + 1] : lines.FirstOrDefault(l => l.Length > 80 && !l.Contains('|')) ?? "";
        p.Summary = summary.Length > 240 ? summary[..240] : summary;
        ExtractDetails(text, lines, p);
        return p;
    }

    // ---------- details vendors ask for ----------
    private static readonly Regex Degree = new(@"\b(B\.?\s?Tech|B\.?\s?E\b|B\.?\s?S\b|B\.?\s?Sc|BCA|Bachelor'?s?|Master'?s?|M\.?\s?Tech|M\.?\s?S\b|M\.?\s?Sc|MCA|MBA|M\.?\s?E\b|Ph\.?\s?D)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the line names a degree (B.Tech, Bachelor of …, MS, MBA, PhD …).</summary>
    public static bool IsDegree(string line) => Degree.IsMatch(line);

    /// <summary>LinkedIn link, passport number (only when labelled), highest degree, certifications and availability.</summary>
    public static void ExtractDetails(string text, IReadOnlyList<string> lines, ParsedResume p)
    {
        var li = Regex.Match(text, @"(?:https?://)?(?:[a-z]{2,3}\.)?linkedin\.com/in/[A-Za-z0-9\-_%]+", RegexOptions.IgnoreCase);
        if (li.Success) p.LinkedInUrl = LinkedIn.ProfileUrl(li.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? li.Value : "https://" + li.Value) ?? "";

        var pp = Regex.Match(text, @"passport\s*(?:no\.?|number|num|#)?\s*[:\-]?\s*([A-Z]{0,2}[0-9][A-Z0-9]{5,10})\b", RegexOptions.IgnoreCase);
        if (pp.Success) p.Passport = pp.Groups[1].Value.ToUpperInvariant();

        // Degrees: prefer the highest (PhD > Master > Bachelor), taking the whole line ("MS Computer Science, UNT, 2014").
        static int Rank(string l) => Regex.IsMatch(l, @"ph\.?\s?d", RegexOptions.IgnoreCase) ? 3
            : Regex.IsMatch(l, @"\b(master|m\.?\s?tech|m\.?\s?s\b|m\.?\s?sc|mca|mba|m\.?\s?e\b)", RegexOptions.IgnoreCase) ? 2 : 1;
        var degrees = lines.Where(l => l.Length <= 200 && Degree.IsMatch(l) && !Regex.IsMatch(l, @"^(education|academic)", RegexOptions.IgnoreCase)).ToList();
        if (degrees.Count > 0) p.Education = Regex.Replace(degrees.OrderByDescending(Rank).First(), @"\s+", " ").Trim(' ', '•', '-', '*', '·');

        var certs = lines.Where(l => l.Length is > 8 and <= 160 && Regex.IsMatch(l, @"certified|certification|certificate", RegexOptions.IgnoreCase)
                                     && !Regex.IsMatch(l, @"^(certifications?|certificates?)\s*:?\s*$", RegexOptions.IgnoreCase))
            .Select(l => Regex.Replace(l, @"^(certifications?\s*:\s*|[•\-*·▪]\s*)", "", RegexOptions.IgnoreCase).Trim()).Distinct().Take(5).ToList();
        if (certs.Count > 0) { var all = string.Join("; ", certs); p.Certifications = all.Length > 480 ? all[..480] : all; }

        var av = Regex.Match(text, @"available\s+(immediately|now|(?:from|after|by)\s+[A-Za-z]{3,9}\.?\s+\d{1,2}(?:,?\s*\d{4})?|in\s+\d+\s+weeks?|with\s+\d+\s+weeks?\s+notice)", RegexOptions.IgnoreCase);
        if (av.Success) p.Availability = char.ToUpperInvariant(av.Groups[1].Value[0]) + av.Groups[1].Value[1..];
        else if (Regex.IsMatch(text, @"\b(\d+)\s*weeks?\s*notice", RegexOptions.IgnoreCase)) p.Availability = Regex.Match(text, @"\b\d+\s*weeks?\s*notice", RegexOptions.IgnoreCase).Value;
    }

    // ---------- candidate name ----------
    // Words that never belong in a person's name: section headings, job words, contact labels.
    private static readonly Regex NotAName = new(
        @"\b(resume|résumé|curriculum|vitae|cv|bio|summary|profile|objective|experience|education|skills?|technical|professional|career|contact|" +
        @"email|e-mail|phone|mobile|cell|address|linkedin|github|portfolio|references?|certifications?|projects?|employment|work|history|" +
        @"developer|engineer|architect|analyst|consultant|tester|administrator|admin|scientist|manager|lead|senior|sr|junior|jr|principal|staff|" +
        @"specialist|associate|intern|director|devops|sdet|qa|full|stack|frontend|backend|software|data|cloud|java|python|react|net|sql|" +
        @"united|states|usa|remote|visa|citizen|green|card|available|immediately|open|relocation|years?|yrs)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Credentials = new(@"(,?\s*\b(PMP|CSM|CSPO|MBA|MS|M\.S\.|BS|B\.S\.|PhD|Ph\.D\.|CPA|CISSP|SAFe|ITIL)\b\.?)+\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex NameShape = new(@"^\p{L}[\p{L}'’.\-]*(\s+\p{L}[\p{L}'’.\-]*){1,4}$");

    /// <summary>
    /// Finds the candidate's name: an explicit "Name:" label first; otherwise the best name-looking piece of the first
    /// lines (headers often put name, title and contacts on one line separated by | or •), preferring one that agrees
    /// with the email address; last resort, the email's local part ("ravi.kumar@…" → "Ravi Kumar").
    /// </summary>
    public static string FindName(IReadOnlyList<string> lines, string? email)
    {
        var label = lines.Take(25).Select(l => Regex.Match(l, @"^\s*(?:full\s+|candidate\s+|consultant\s+)?name\s*[:\-–]\s*(.+)$", RegexOptions.IgnoreCase))
            .FirstOrDefault(m => m.Success);
        if (label != null && Clean(label.Groups[1].Value) is { } labelled && NameShape.IsMatch(labelled)) return Pretty(labelled);

        var local = (email ?? "").Split('@')[0].ToLowerInvariant();
        var localWords = Regex.Split(local, @"[._\-0-9]+").Where(w => w.Length >= 2).ToList();

        string? best = null; var bestScore = 0.0;
        for (var i = 0; i < Math.Min(lines.Count, 12); i++)
        {
            foreach (var raw in Regex.Split(UnspaceLetters(lines[i]), @"\s*[|•·●▪♦–—\t]\s*|\s{3,}|\s+-\s+|,\s+(?=\S+@)"))
            {
                var seg = Clean(raw);
                if (seg is null || !NameShape.IsMatch(seg) || NotAName.IsMatch(seg) || Regex.IsMatch(seg, @",\s*[A-Z]{2}$")) continue;
                var words = seg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Any(w => w.Length > 20) || FindSkills(seg).Count > 0) continue;
                var score = 10.0 - i;                                                        // earlier lines first
                if (words.All(w => char.IsUpper(w[0]))) score += 3;                         // Title Case or ALL CAPS
                if (seg == lines[i].Trim()) score += 2;                                      // the whole line is the name
                var hits = localWords.Count(lw => words.Any(w => w.ToLowerInvariant().Trim('.').StartsWith(lw) || lw.StartsWith(w.ToLowerInvariant().Trim('.'))));
                score += hits * 6;                                                           // agrees with the email
                if (score > bestScore) { best = seg; bestScore = score; }
            }
        }
        if (best != null) return Pretty(best);

        // Last resort: "ravi.kumar@…" / "ravi_kumar88@…" → "Ravi Kumar" (a single run-together word isn't trusted).
        return localWords.Count >= 2 && localWords.All(w => w.All(char.IsLetter)) ? Pretty(string.Join(' ', localWords.Take(4))) : "";

        static string? Clean(string s)
        {
            s = Regex.Replace(s, @"^(resume|résumé|cv|curriculum vitae)\s*(of|[:\-–])?\s*", "", RegexOptions.IgnoreCase);
            s = Credentials.Replace(s, "");
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', ',', ':', '-', '–', '.');
            return s.Length is >= 3 and <= 60 ? s : null;
        }
    }

    /// <summary>"R A V I   K U M A R" (letter-spaced headings in PDFs) → "RAVI KUMAR".</summary>
    private static string UnspaceLetters(string line) =>
        Regex.IsMatch(line, @"^\s*(\p{L} ){3,}\p{L}") ? Regex.Replace(Regex.Replace(line, @"(?<=\p{L}) (?=\p{L}( |$))", ""), @"\s{2,}", " ") : line;

    /// <summary>ALL CAPS or all lower → Title Case; mixed case (McDonald, DeSouza) is kept as written.</summary>
    private static string Pretty(string name) =>
        name == name.ToUpperInvariant() || name == name.ToLowerInvariant()
            ? string.Join(' ', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]))
            : name;

    public static RequirementInput ExtractRequirement(string t)
    {
        string G(string pattern) { var m = Regex.Match(t, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline); return m.Success ? m.Groups[1].Value.Trim() : ""; }
        var visas = Domain.Visas.Where(v => Regex.IsMatch(t, $@"\b{Regex.Escape(v)}\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(t, $@"no\s+{Regex.Escape(v)}", RegexOptions.IgnoreCase)).ToList();
        var rate = G(@"\$\s?(\d{2,3})");
        return new RequirementInput(
            Title: G(@"(?:role|position|title)\s*[:\-]\s*(.+)$"),
            Client: Regex.Match(t, @"client\s+([A-Z][\w&]+(?:\s[A-Z][\w&]+){0,2})").Groups[1].Value,
            Vendor: Regex.Match(t, @"through\s+([A-Z][\w&]+(?:\s[A-Z][\w&]+){0,2})").Groups[1].Value is { Length: > 0 } v ? v : G(@"\|\s*(.+)$"),
            Tier: Regex.IsMatch(t, "implementation", RegexOptions.IgnoreCase) ? "Implementation partner" : Regex.IsMatch(t, "prime", RegexOptions.IgnoreCase) ? "Prime vendor" : Regex.IsMatch(t, "direct", RegexOptions.IgnoreCase) ? "Direct" : "Tier-1",
            Location: G(@"location\s*[:\-]\s*([^(\n]+)"),
            Mode: Regex.IsMatch(t, "remote", RegexOptions.IgnoreCase) ? "Remote" : Regex.IsMatch(t, "hybrid", RegexOptions.IgnoreCase) ? "Hybrid" : "Onsite",
            Type: Regex.IsMatch(t, @"\bW2\b") ? "W2" : t.Contains("1099") ? "1099" : "C2C",
            Rate: decimal.TryParse(rate, out var r) ? r : 0,
            Skills: FindSkills(t), Visas: visas.Count > 0 ? visas : ["Any"], Description: t, Source: "SourcingAgent", Channels: null);
    }

    public static string JobDescription(JdInput j)
    {
        var skills = j.Skills ?? new();
        var sb = new StringBuilder();
        sb.AppendLine(j.Title);
        sb.AppendLine($"Client: {j.Client} | Location: {j.Location} ({j.Mode}) | Contract: {j.Type} | Rate: ${j.Rate:0}/hr");
        sb.AppendLine($"Visa: {string.Join(", ", j.Visas ?? ["Any"])}");
        sb.AppendLine().AppendLine("About the role");
        sb.AppendLine($"We are looking for an experienced {j.Title} to join {j.Client}'s delivery team through {j.Vendor}. You will design, build and support production systems with {string.Join(", ", skills.Take(3))}.");
        sb.AppendLine().AppendLine("Key responsibilities");
        sb.AppendLine($"• Design and deliver features using {string.Join(", ", skills)}");
        sb.AppendLine("• Work with product owners and architects in an Agile/Scrum team");
        sb.AppendLine("• Own code quality, reviews, testing and production support");
        sb.AppendLine().AppendLine("Required skills");
        foreach (var s in skills) sb.AppendLine($"• Strong hands-on experience with {s}");
        sb.AppendLine().AppendLine("How to apply");
        sb.Append("Send your updated resume, visa copy, current location and expected rate. RTR required for submission.");
        return sb.ToString();
    }

    public static string Pitch(ConsultantDto c, RequirementDto j, MatchResult m, string sender) =>
        $"Hi,\n\nPlease find {c.Name} for the {j.Title} role with {j.Client}.\n\n" +
        $"• {c.Exp}+ years as {c.Title}\n• Strong in {string.Join(", ", m.Hit.Count > 0 ? m.Hit : c.Skills.Take(4))}\n" +
        $"• Visa: {c.Visa} | Location: {c.Location}{(c.Relocate ? " (open to relocate)" : "")}\n• Available immediately; RTR attached\n\n" +
        (m.Miss.Count > 0 ? $"Gaps to cover in screening: {string.Join(", ", m.Miss)}.\n\n" : "") + $"Thanks,\n{sender}";

    public static string InterviewPrep(SubmissionDto s, ConsultantDto c, RequirementDto j, MatchResult m)
    {
        var sb = new StringBuilder($"Interview prep: {c.Name} → {j.Title} ({j.Client})\n\nLikely technical questions\n");
        var i = 1;
        foreach (var k in j.Skills) sb.AppendLine($"{i++}. Walk me through a production system where you used {k}. What went wrong and how did you fix it?");
        sb.AppendLine($"{i}. How would you design a scalable, fault-tolerant solution for {j.Client}'s use case?");
        sb.AppendLine("\nGaps to prepare for");
        if (m.Miss.Count == 0) sb.AppendLine("• None. Strong skill coverage.");
        foreach (var k in m.Miss) sb.AppendLine($"• {k}: be honest, cover related experience and how fast you can ramp up");
        sb.AppendLine($"\nTalking points\n• {c.Exp}+ years as {c.Title}\n• Highlight: {string.Join(", ", c.Skills.Take(4))}\n• Domain fit for {j.Client}");
        sb.Append($"\nLogistics\n• Confirm time zone, video link and interviewer names with {j.Vendor}\n• Quiet room, camera on, resume open\n• Don't discuss rate. Your recruiter handles it.");
        return sb.ToString();
    }

    public static string Briefing(string name, int bench, int active, int open, List<Insight> insights)
    {
        var risks = insights.Where(i => i.Type == "risk").Select(i => i.Title.ToLowerInvariant()).ToList();
        var opp = insights.FirstOrDefault(i => i.Type == "opportunity");
        return $"• {name}, today you have {bench} on bench, {active} active submissions and {open} open requirements.\n" +
               (risks.Count > 0 ? $"• Priority: {string.Join("; ", risks)}.\n" : "• No risks flagged.\n") +
               (opp != null ? $"• Biggest opportunity: {opp.Detail}.\n" : "") +
               "• Next step: clear stuck submissions first, then submit the strongest matches.";
    }
}
