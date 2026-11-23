using System.Text;
using System.Text.RegularExpressions;

namespace CollaborateBench.Api.Services;

/// <summary>
/// Pure helpers for the LinkedIn side panel: canonical URLs (so the same profile or job is never saved twice),
/// where a candidate lives (for privacy-law reminders), and turning what's on the page into text for the AI parser.
/// </summary>
public static partial class LinkedIn
{
    private static bool IsLinkedInHost(Uri u) =>
        u.Host.Equals("linkedin.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>https://www.linkedin.com/in/{slug} (lower case, no query or trailing parts), or null if it isn't a profile URL.</summary>
    public static string? ProfileUrl(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) || !IsLinkedInHost(u)) return null;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("in", StringComparison.OrdinalIgnoreCase)) return null;
        var slug = Uri.UnescapeDataString(parts[1]).Trim().ToLowerInvariant();
        if (slug.Length is 0 or > 100 || slug.Any(char.IsWhiteSpace)) return null;
        return "https://www.linkedin.com/in/" + Uri.EscapeDataString(slug);
    }

    /// <summary>https://www.linkedin.com/jobs/view/{id}/ from a job page or a search page with currentJobId, or null.</summary>
    public static string? JobUrl(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) || !IsLinkedInHost(u)) return null;
        var view = JobViewId().Match(u.AbsolutePath);
        var id = view.Success ? view.Groups[1].Value : CurrentJobId().Match(u.Query) is { Success: true } q ? q.Groups[1].Value : null;
        return id is null ? null : $"https://www.linkedin.com/jobs/view/{id}/";
    }

    [GeneratedRegex(@"/jobs/view/(?:[^/]*?-)?(\d{5,})", RegexOptions.IgnoreCase)] private static partial Regex JobViewId();
    [GeneratedRegex(@"[?&]currentJobId=(\d{5,})", RegexOptions.IgnoreCase)] private static partial Regex CurrentJobId();

    // ---------- where the candidate lives ----------
    private static readonly HashSet<string> EuEea = new(StringComparer.OrdinalIgnoreCase)
    {
        "Austria", "Belgium", "Bulgaria", "Croatia", "Cyprus", "Czechia", "Czech Republic", "Denmark", "Estonia", "Finland", "France",
        "Germany", "Greece", "Hungary", "Ireland", "Italy", "Latvia", "Lithuania", "Luxembourg", "Malta", "Netherlands", "The Netherlands",
        "Poland", "Portugal", "Romania", "Slovakia", "Slovenia", "Spain", "Sweden", "Iceland", "Liechtenstein", "Norway",
    };
    private static readonly HashSet<string> Uk = new(StringComparer.OrdinalIgnoreCase) { "United Kingdom", "UK", "England", "Scotland", "Wales", "Northern Ireland" };
    private static readonly HashSet<string> Us = new(StringComparer.OrdinalIgnoreCase) { "United States", "United States of America", "USA", "US" };
    private static readonly HashSet<string> Elsewhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "India", "Canada", "Mexico", "Brazil", "Argentina", "Colombia", "Philippines", "Pakistan", "Bangladesh", "Sri Lanka", "Nepal",
        "Australia", "New Zealand", "Singapore", "Malaysia", "Indonesia", "Vietnam", "China", "Japan", "South Korea", "United Arab Emirates",
        "Saudi Arabia", "Qatar", "Israel", "Turkey", "Egypt", "Nigeria", "Kenya", "South Africa", "Ukraine", "Switzerland", "Serbia",
    };

    /// <summary>EU/EEA, UK, US, Other (a known non-US country) or null when the location doesn't say (e.g. "Greater Boston").</summary>
    public static string? Region(string? location)
    {
        var country = (location ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrEmpty(country)) return null;
        if (EuEea.Contains(country)) return "EU/EEA";
        if (Uk.Contains(country)) return "UK";
        if (Us.Contains(country) || UsStates.Contains(country)) return "US";
        return Elsewhere.Contains(country) ? "Other" : null;
    }

    private static readonly HashSet<string> UsStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut", "Delaware", "Florida", "Georgia", "Hawaii",
        "Idaho", "Illinois", "Indiana", "Iowa", "Kansas", "Kentucky", "Louisiana", "Maine", "Maryland", "Massachusetts", "Michigan",
        "Minnesota", "Mississippi", "Missouri", "Montana", "Nebraska", "Nevada", "New Hampshire", "New Jersey", "New Mexico", "New York",
        "North Carolina", "North Dakota", "Ohio", "Oklahoma", "Oregon", "Pennsylvania", "Rhode Island", "South Carolina", "South Dakota",
        "Tennessee", "Texas", "Utah", "Vermont", "Virginia", "Washington", "West Virginia", "Wisconsin", "Wyoming", "District of Columbia",
    };

    /// <summary>Years since the earliest year in the experience section ("Jan 2014 - Present" → years since 2014), 0 if none.</summary>
    public static int YearsFromExperience(string? experience)
    {
        var now = DateTime.UtcNow.Year;
        var years = Year().Matches(experience ?? "").Select(m => int.Parse(m.Value)).Where(y => y >= 1970 && y <= now).ToList();
        return years.Count == 0 ? 0 : Math.Clamp(now - years.Min(), 0, 50);
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")] private static partial Regex Year();

    /// <summary>"Senior Java Developer | Spring Boot | AWS" → "Senior Java Developer"; "Data Engineer at Acme" → "Data Engineer".</summary>
    public static string TitleFromHeadline(string? headline)
    {
        var h = (headline ?? "").Split('|', '•', '·')[0];
        var at = Regex.Match(h, @"\s+(at|@)\s+", RegexOptions.IgnoreCase);
        if (at.Success) h = h[..at.Index];
        h = h.Trim(' ', '-', ',');
        return h.Length > 120 ? h[..120] : h;
    }

    /// <summary>The profile as plain text, in resume order, for the resume parser (capped so the AI call stays small).</summary>
    public static string ProfileText(string name, string? headline, string? location, string? about, string? experience, string? education, IEnumerable<string> skills)
    {
        var sb = new StringBuilder().AppendLine(name);
        if (!string.IsNullOrWhiteSpace(headline)) sb.AppendLine(headline.Trim());
        if (!string.IsNullOrWhiteSpace(location)) sb.AppendLine(location.Trim());
        void Section(string title, string? body, int max)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            body = body.Trim();
            sb.AppendLine().AppendLine(title).AppendLine(body.Length > max ? body[..max] : body);
        }
        Section("SUMMARY", about, 3000);
        Section("EXPERIENCE", experience, 7000);
        Section("EDUCATION", education, 1500);
        var s = skills.ToList();
        if (s.Count > 0) sb.AppendLine().AppendLine("SKILLS").AppendLine(string.Join(", ", s));
        return sb.ToString();
    }

    /// <summary>Trims page text to a length the database accepts, or null when empty.</summary>
    public static string? Clip(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;
    }
}
