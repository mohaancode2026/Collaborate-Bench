using System.Text.RegularExpressions;
using CollaborateBench.Api.Models;

namespace CollaborateBench.Api.Services;

/// <summary>{{group.field}} merge fields for vendor emails. Pay rates are deliberately not available.</summary>
public static class MergeFields
{
    public static readonly MergeField[] All =
    [
        new("contact.firstName", "Contact first name (\"there\" if unknown)", "contact", "Priya"),
        new("contact.name", "Contact full name", "contact", "Priya Raman"),
        new("vendor.name", "Vendor company", "vendor", "TEKsystems"),
        new("consultant.name", "Consultant name", "consultant", "Ravi Kumar"),
        new("consultant.firstName", "Consultant first name", "consultant", "Ravi"),
        new("consultant.title", "Consultant title", "consultant", "Senior Java Developer"),
        new("consultant.experience", "Years of experience", "consultant", "11"),
        new("consultant.skills", "Top skills", "consultant", "Java, Spring Boot, Microservices, AWS, Kafka"),
        new("consultant.visa", "Visa / work authorization", "consultant", "H1B"),
        new("consultant.location", "Current location", "consultant", "Dallas, TX"),
        new("consultant.relocation", "Relocation", "consultant", "Open to relocate"),
        new("consultant.summary", "Hotlist summary", "consultant", "11+ yrs Java/Spring microservices on AWS for banking."),
        new("requirement.title", "Job title", "requirement", "Senior Java Developer"),
        new("requirement.client", "End client", "requirement", "Bank of America"),
        new("requirement.location", "Job location", "requirement", "Charlotte, NC"),
        new("requirement.mode", "Remote / Hybrid / Onsite", "requirement", "Hybrid"),
        new("requirement.type", "C2C / W2 / 1099", "requirement", "C2C"),
        new("requirement.rate", "Bill rate", "requirement", "$85/hr"),
        new("me.name", "Your name", "me", "Megha Iyer"),
        new("me.firstName", "Your first name", "me", "Megha"),
        new("me.email", "Your email", "me", "megha@company.com"),
        new("today", "Today's date", "other", "Nov 20, 2026"),
    ];
    public static readonly HashSet<string> Keys = new(All.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
    public static readonly Regex Token = new(@"\{\{\s*([A-Za-z]+(?:\.[A-Za-z]+)?)\s*\}\}", RegexOptions.Compiled);

    public static IEnumerable<string> Used(string text) => Token.Matches(text).Select(m => m.Groups[1].Value);

    /// <summary>"consultant" / "requirement" when the template needs one picked before it can be filled in.</summary>
    public static List<string> Needs(string subject, string body) =>
        Used(subject + "\n" + body).Select(k => k.Split('.')[0].ToLowerInvariant()).Where(g => g is "consultant" or "requirement").Distinct().OrderBy(g => g).ToList();

    public static RenderResult Render(string subject, string body, IReadOnlyDictionary<string, string?> values)
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string Fill(string text) => Token.Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            if (!Keys.Contains(key)) { unknown.Add(key); return m.Value; }
            if (values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            missing.Add(key);
            return m.Value;   // left visible so the user notices and fills it in
        });
        return new RenderResult(Fill(subject), Fill(body), missing.ToList(), unknown.ToList());
    }
}
