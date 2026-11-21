using CollaborateBench.Api.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

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

    public static byte[] Build(ResumeDoc d)
    {
        using var ms = new MemoryStream();
        using (var word = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            word.PackageProperties.Title = $"{d.Name} - Resume";
            word.PackageProperties.Creator = "Collaborate-Bench";

            body.Append(Para([Run(d.Name, 18, bold: true, color: Ink)], after: 40));
            if (d.Headline.Length > 0) body.Append(Para([Run(d.Headline, 11.5, color: Muted)], after: 40));
            var contact = new[] { d.Location, d.Email, d.Phone }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (contact.Count > 0) body.Append(Para([Run(string.Join("  |  ", contact!), 10, color: Muted)], after: 120));

            if (d.Summary.Length > 0) { body.Append(Heading("Professional Summary")); body.Append(Para([Run(d.Summary)])); }

            if (d.Skills.Count > 0)
            {
                body.Append(Heading("Technical Skills"));
                foreach (var g in d.Skills)
                    body.Append(Para([Run($"{g.Category}: ", bold: true), Run(string.Join(", ", g.Items))], after: 40));
            }

            if (d.Experience.Count > 0 || !string.IsNullOrWhiteSpace(d.AdditionalText))
            {
                body.Append(Heading("Professional Experience"));
                foreach (var j in d.Experience)
                {
                    var dates = string.Join(" – ", new[] { j.Start, j.End }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    // Title and company on the left, dates right-aligned on the same line.
                    var line = Para([Run(j.Title, bold: true, color: Ink), Run(j.Company.Length > 0 ? $"  |  {j.Company}" : "", bold: true), new Run(new TabChar()), Run(dates, color: Muted)],
                        before: 120, after: 20, rightTab: true);
                    body.Append(line);
                    if (!string.IsNullOrWhiteSpace(j.Location)) body.Append(Para([Run(j.Location!, 10, italic: true, color: Muted)], after: 40));
                    foreach (var b in j.Bullets) body.Append(Bullet(b));
                    if (!string.IsNullOrWhiteSpace(j.Environment)) body.Append(Para([Run("Environment: ", 10, bold: true), Run(j.Environment!, 10, italic: true)], before: 40));
                }
                // Built-in engine: the original experience text, unchanged.
                foreach (var l in (d.AdditionalText ?? "").Replace("\r", "").Split('\n'))
                {
                    var t = l.Trim();
                    if (t.Length == 0) continue;
                    body.Append(t.StartsWith('•') || t.StartsWith("- ") || t.StartsWith("* ") ? Bullet(t.TrimStart('•', '-', '*', ' ')) : Para([Run(t)], after: 40));
                }
            }

            if (d.Education.Count > 0)
            {
                body.Append(Heading("Education"));
                foreach (var e in d.Education)
                    body.Append(Para([Run(e.Degree, bold: true), Run(string.Join(", ", new[] { e.School, e.Year }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } rest ? $"  |  {rest}" : "")], after: 40));
            }
            if (d.Certifications.Count > 0)
            {
                body.Append(Heading("Certifications"));
                foreach (var c in d.Certifications) body.Append(Bullet(c));
            }

            body.Append(new SectionProperties(
                new PageSize { Width = 12240U, Height = 15840U },
                new PageMargin { Top = 1080, Bottom = 1080, Left = 1080U, Right = 1080U, Header = 720U, Footer = 720U }));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    // OpenXML is strict about child order (Word reports "unreadable content" otherwise), so properties are built in schema order:
    // run: rFonts, b, i, color, sz;  paragraph: keepNext, pBdr, tabs, spacing, ind.
    private static Run Run(string text, double size = 10.5, bool bold = false, bool italic = false, string? color = null)
    {
        var rp = new RunProperties(new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font });
        if (bold) rp.Append(new Bold());
        if (italic) rp.Append(new Italic());
        if (color != null) rp.Append(new Color { Val = color });
        rp.Append(new FontSize { Val = ((int)(size * 2)).ToString() });
        return new Run(rp, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Paragraph Para(OpenXmlElement[] runs, int before = 0, int after = 80, bool rightTab = false,
        bool keepNext = false, bool border = false, TabStop? tab = null, Indentation? indent = null)
    {
        var pp = new ParagraphProperties();
        if (keepNext) pp.Append(new KeepNext());
        if (border) pp.Append(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "CBD5E1" }));
        if (rightTab) pp.Append(new Tabs(new TabStop { Val = TabStopValues.Right, Position = ContentWidth }));
        else if (tab != null) pp.Append(new Tabs(tab));
        pp.Append(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = "264", LineRule = LineSpacingRuleValues.Auto });
        if (indent != null) pp.Append(indent);
        var p = new Paragraph(pp);
        p.Append(runs);
        return p;
    }

    private static Paragraph Heading(string text) =>
        Para([Run(text.ToUpperInvariant(), 11, bold: true, color: Ink)], before: 200, after: 80, keepNext: true, border: true);

    // Plain "•" with a hanging indent: renders the same in Word, Google Docs and ATS parsers, no numbering part needed.
    private static Paragraph Bullet(string text) =>
        Para([Run("•"), new Run(new TabChar()), Run(text)], after: 30, tab: new TabStop { Val = TabStopValues.Left, Position = 360 }, indent: new Indentation { Left = "360", Hanging = "240" });
}
