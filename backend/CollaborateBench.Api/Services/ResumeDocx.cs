using CollaborateBench.Api.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Rx = System.Text.RegularExpressions.Regex;
using RxO = System.Text.RegularExpressions.RegexOptions;

namespace CollaborateBench.Api.Services;

/// <summary>
/// Renders a <see cref="ResumeDoc"/> as a clean, ATS-friendly Word document: a single column, real text (no tables or text
/// boxes), standard section headings and Calibri. Vendors and applicant tracking systems read this layout reliably.
/// </summary>
public static class ResumeDocx
{
    private const string Font = "Calibri";
    private const string Ink = "1E293B";
    private const string Muted = "475569";
    private const int ContentWidth = 10080;   // twips between 0.75" margins on US Letter

    /// <summary>Per-template sizes (points) and spacing (twips). Compact fits more on a page; Modern adds colour.</summary>
    private sealed record Style(double Name, double Headline, double Body, double Small, double HeadingSize, int Gap, string Accent, bool AccentName, bool AccentTitles);

    public static byte[] Build(ResumeDoc d)
    {
        var accent = System.Text.RegularExpressions.Regex.IsMatch(d.Accent ?? "", "^[0-9A-Fa-f]{6}$") ? d.Accent!.ToUpperInvariant() : Ink;
        var st = (d.Template ?? "classic") switch
        {
            "modern" => new Style(20, 12, 10.5, 9.5, 11.5, 220, accent, AccentName: true, AccentTitles: true),
            "compact" => new Style(16, 10.5, 9.5, 9, 10, 140, accent, AccentName: false, AccentTitles: false),
            _ => new Style(18, 11.5, 10.5, 10, 11, 200, accent, AccentName: false, AccentTitles: false),
        };
        _body = st.Body;
        using var ms = new MemoryStream();
        using (var word = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            word.PackageProperties.Title = $"{d.Name} - Resume";
            word.PackageProperties.Creator = "Collaborate-Bench";
            Paragraph Heading(string text) =>
                Para([Run(text.ToUpperInvariant(), st.HeadingSize, bold: true, color: st.Accent)], before: st.Gap, after: 80, keepNext: true,
                     border: true, borderColor: d.Template == "modern" ? st.Accent : "CBD5E1", borderSize: d.Template == "modern" ? 12U : 6U);

            body.Append(Para([Run(d.Name, st.Name, bold: true, color: st.AccentName ? st.Accent : Ink)], after: 40));
            if (d.Headline.Length > 0) body.Append(Para([Run(d.Headline, st.Headline, color: d.Template == "modern" ? Ink : Muted, bold: d.Template == "modern")], after: 40));
            var contact = new[] { d.Location, d.Email, d.Phone }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (contact.Count > 0) body.Append(Para([Run(string.Join("  |  ", contact!), st.Small, color: Muted)], after: 120));

            if (d.Shows("summary") && d.Summary.Length > 0) { body.Append(Heading("Professional Summary")); body.Append(Para([Run(d.Summary)])); }

            if (d.Shows("skills") && d.Skills.Count > 0)
            {
                body.Append(Heading("Technical Skills"));
                foreach (var g in d.Skills)
                    body.Append(Para([Run($"{g.Category}: ", bold: true), Run(string.Join(", ", g.Items))], after: 40));
            }

            if (d.Shows("experience") && (d.Experience.Count > 0 || !string.IsNullOrWhiteSpace(d.AdditionalText)))
            {
                body.Append(Heading("Professional Experience"));
                foreach (var j in d.Experience)
                {
                    var dates = string.Join(" – ", new[] { j.Start, j.End }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    // Title and company on the left, dates right-aligned on the same line.
                    var line = Para([Run(j.Title, bold: true, color: st.AccentTitles ? st.Accent : Ink), Run(j.Company.Length > 0 ? $"  |  {j.Company}" : "", bold: true), new Run(new TabChar()), Run(dates, color: Muted)],
                        before: 120, after: 20, rightTab: true);
                    body.Append(line);
                    if (!string.IsNullOrWhiteSpace(j.Location)) body.Append(Para([Run(j.Location!, st.Small, italic: true, color: Muted)], after: 40));
                    foreach (var b in j.Bullets) body.Append(Bullet(b));
                    if (!string.IsNullOrWhiteSpace(j.Environment)) body.Append(Para([Run("Environment: ", st.Small, bold: true), Run(j.Environment!, st.Small, italic: true)], before: 40));
                }
                // Built-in engine: the original experience text, worded exactly as written; laid out as job headings and bullets.
                foreach (var (kind, t) in ClassifyRaw(d.AdditionalText))
                {
                    if (kind == "heading") body.Append(Para([Run(t, st.Body, bold: true, color: st.AccentTitles ? st.Accent : Ink)], before: 120, after: 20, keepNext: true));
                    else if (kind == "meta") body.Append(Para([Run(t, st.Small, italic: true, color: Muted)], after: 40, keepNext: true));
                    else if (kind == "bullet") body.Append(Bullet(t));
                    else body.Append(Para([Run(t)], after: 40));
                }
            }

            if (d.Shows("education") && d.Education.Count > 0)
            {
                body.Append(Heading("Education"));
                foreach (var e in d.Education)
                    body.Append(Para([Run(e.Degree, bold: true), Run(string.Join(", ", new[] { e.School, e.Year }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } rest ? $"  |  {rest}" : "")], after: 40));
            }
            if (d.Shows("certifications") && d.Certifications.Count > 0)
            {
                body.Append(Heading("Certifications"));
                foreach (var c in d.Certifications) body.Append(Bullet(c));
            }

            var margin = d.Template == "compact" ? 864 : 1080;   // 0.6" or 0.75"
            body.Append(new SectionProperties(
                new PageSize { Width = 12240U, Height = 15840U },
                new PageMargin { Top = margin, Bottom = margin, Left = (uint)margin, Right = (uint)margin, Header = 720U, Footer = 720U }));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Reads the original experience text line by line (same rules as the on-screen preview): a line with a date range
    /// ("04/2017 to 09/2018", "2019 – Present"), a short all-caps line or "Client: …" is a heading; the short line right after
    /// a heading (company, city) is meta; bullet lines and full sentences are bullets.
    /// </summary>
    public static IEnumerable<(string Kind, string Text)> ClassifyRaw(string? text)
    {
        string? prev = null;
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            var dated = t.Length <= 120 && Rx.IsMatch(t, @"\b(19|20)\d{2}\b|\b\d{1,2}/\d{2,4}\b") && Rx.IsMatch(t, @"(to|–|—|-)\s*(present|current|till date|now|\d)", RxO.IgnoreCase);
            var caps = t.Length is >= 3 and <= 60 && t == t.ToUpperInvariant() && t.Any(char.IsLetter) && !t.Contains('.');
            string kind;
            if (Rx.IsMatch(t, @"^[•\-*▪·]\s*")) kind = "bullet";
            else if (dated || caps || Rx.IsMatch(t, @"^(client|employer|company|project|role)\s*:", RxO.IgnoreCase)) kind = "heading";
            else if (prev == "heading" && t.Length <= 80 && !Rx.IsMatch(t, @"[.!?]$")) kind = "meta";
            else if (Rx.IsMatch(t, @"[.!?]$") && t.Length >= 25) kind = "bullet";
            else kind = "text";
            prev = kind;
            yield return (kind, kind == "bullet" ? Rx.Replace(t, @"^[•\-*▪·]\s*", "") : kind == "heading" ? t.TrimEnd(':') : t);
        }
    }

    // Body text size for the current document (set per template at the start of Build; Build is not called concurrently on one thread).
    [ThreadStatic] private static double _body;

    // OpenXML is strict about child order (Word reports "unreadable content" otherwise), so properties are built in schema order:
    // run: rFonts, b, i, color, sz;  paragraph: keepNext, pBdr, tabs, spacing, ind.
    private static Run Run(string text, double size = 0, bool bold = false, bool italic = false, string? color = null)
    {
        if (size <= 0) size = _body > 0 ? _body : 10.5;
        var rp = new RunProperties(new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font });
        if (bold) rp.Append(new Bold());
        if (italic) rp.Append(new Italic());
        if (color != null) rp.Append(new Color { Val = color });
        rp.Append(new FontSize { Val = ((int)(size * 2)).ToString() });
        return new Run(rp, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Paragraph Para(OpenXmlElement[] runs, int before = 0, int after = 80, bool rightTab = false,
        bool keepNext = false, bool border = false, TabStop? tab = null, Indentation? indent = null, string borderColor = "CBD5E1", uint borderSize = 6)
    {
        var pp = new ParagraphProperties();
        if (keepNext) pp.Append(new KeepNext());
        if (border) pp.Append(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = borderSize, Space = 1, Color = borderColor }));
        if (rightTab) pp.Append(new Tabs(new TabStop { Val = TabStopValues.Right, Position = ContentWidth }));
        else if (tab != null) pp.Append(new Tabs(tab));
        pp.Append(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = "264", LineRule = LineSpacingRuleValues.Auto });
        if (indent != null) pp.Append(indent);
        var p = new Paragraph(pp);
        p.Append(runs);
        return p;
    }

    // Plain "•" with a hanging indent: renders the same in Word, Google Docs and ATS parsers, no numbering part needed.
    private static Paragraph Bullet(string text) =>
        Para([Run("•"), new Run(new TabChar()), Run(text)], after: 30, tab: new TabStop { Val = TabStopValues.Left, Position = 360 }, indent: new Indentation { Left = "360", Hanging = "240" });
}
