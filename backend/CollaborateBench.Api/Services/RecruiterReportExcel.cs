using ClosedXML.Excel;
using CollaborateBench.Api.Models;

namespace CollaborateBench.Api.Services;

/// <summary>Builds the recruiter-wise report as an .xlsx workbook: a summary sheet and a submissions detail sheet.</summary>
public static class RecruiterReportExcel
{
    private static readonly (string Stage, string Label)[] StageCols =
        [("Submitted", "Submitted"), ("Vendor Screen", "Vendor screen"), ("Client Interview", "Interview"), ("Offer", "Offer"), ("MSA / PO", "MSA / PO")];

    private static readonly XLColor Navy = XLColor.FromHtml("#1E293B");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#F1F5F9");
    private static readonly XLColor Line = XLColor.FromHtml("#E2E8F0");
    private const string Money = "\"$\"#,##0.00";

    /// <param name="rangeLabel">Human-readable period, e.g. "Nov 1, 2026 – Nov 19, 2026" or "All time".</param>
    /// <param name="utcOffset">The viewer's offset from UTC, so dates in the file match what they see in the app.</param>
    /// <param name="showMoney">Pay rate and margin columns are only included for roles with margin.view.</param>
    public static byte[] Build(List<RecruiterStat> rows, List<SubmissionDto> subs, string rangeLabel, string scopeLabel,
        string exportedBy, TimeSpan utcOffset, bool showMoney)
    {
        using var wb = new XLWorkbook();
        DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc + utcOffset, DateTimeKind.Unspecified);
        var exportedAt = Local(DateTime.UtcNow);

        // ---------- Sheet 1: summary ----------
        var ws = wb.Worksheets.Add("Recruiter report");
        var headers = new List<string> { "Recruiter", "Role", "Team", "Bench (current)", "Marketing (current)", "Total submissions" };
        headers.AddRange(StageCols.Select(s => s.Label));
        headers.AddRange(["Active", "Placed", "Stuck 3d+"]);
        if (showMoney) headers.Add("Weekly margin");
        headers.Add("Last submission");
        var cols = headers.Count;

        ws.Cell(1, 1).Value = "Recruiter-wise submissions";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(Navy);
        ws.Cell(2, 1).Value = $"Submissions created: {rangeLabel}   ·   View: {scopeLabel}   ·   Bench and marketing are current counts";
        ws.Cell(3, 1).Value = $"Exported by {exportedBy} on {exportedAt:MMM d, yyyy h:mm tt}";
        ws.Range(2, 1, 3, 1).Style.Font.SetFontColor(XLColor.FromHtml("#64748B"));

        const int head = 5;
        for (var i = 0; i < cols; i++) ws.Cell(head, i + 1).Value = headers[i];
        StyleHeader(ws.Range(head, 1, head, cols));

        var r = head + 1;
        foreach (var x in rows)
        {
            var c = 1;
            ws.Cell(r, c++).Value = x.Name;
            ws.Cell(r, c++).Value = x.Role;
            ws.Cell(r, c++).Value = x.Team ?? "";
            ws.Cell(r, c++).Value = x.Bench;
            ws.Cell(r, c++).Value = x.Marketing;
            ws.Cell(r, c++).Value = x.Total;
            foreach (var (stage, _) in StageCols) ws.Cell(r, c++).Value = x.Stages.FirstOrDefault(s => s.Stage == stage)?.Count ?? 0;
            ws.Cell(r, c++).Value = x.Active;
            ws.Cell(r, c++).Value = x.Placed;
            ws.Cell(r, c++).Value = x.Stale;
            if (showMoney) { ws.Cell(r, c).Value = x.WeeklyMargin ?? 0; ws.Cell(r, c++).Style.NumberFormat.Format = Money; }
            if (x.LastSubmissionUtc is { } last) { ws.Cell(r, c).Value = Local(last); ws.Cell(r, c).Style.DateFormat.Format = "mmm d, yyyy"; }
            else ws.Cell(r, c).Value = "None in range";
            r++;
        }

        // Total row with live SUM formulas (numeric columns only).
        var totalRow = r;
        ws.Cell(totalRow, 1).Value = "Total";
        var lastNumericCol = cols - 1;   // everything between Team and "Last submission"
        for (var c = 4; c <= lastNumericCol; c++)
        {
            var colLetter = ws.Column(c).ColumnLetter();
            ws.Cell(totalRow, c).FormulaA1 = rows.Count > 0 ? $"SUM({colLetter}{head + 1}:{colLetter}{totalRow - 1})" : "0";
            if (showMoney && c == cols - 1) ws.Cell(totalRow, c).Style.NumberFormat.Format = Money;
        }
        var tr = ws.Range(totalRow, 1, totalRow, cols);
        tr.Style.Font.SetBold().Fill.SetBackgroundColor(HeaderFill);
        tr.Style.Border.SetTopBorder(XLBorderStyleValues.Thin).Border.SetTopBorderColor(Navy);

        ws.Range(head + 1, 4, totalRow, cols - 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        if (showMoney) ws.Range(head + 1, cols - 1, totalRow, cols - 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
        if (rows.Count > 0) ws.Range(head, 1, totalRow - 1, cols).SetAutoFilter();
        ws.SheetView.FreezeRows(head);
        ws.Columns(1, cols).AdjustToContents(head, totalRow);
        ws.Column(1).Width = Math.Max(ws.Column(1).Width, 22);

        // ---------- Sheet 2: submission detail ----------
        var ds = wb.Worksheets.Add("Submissions");
        var dh = new List<string> { "Recruiter", "Consultant", "Consultant title", "Requirement", "Client", "Vendor", "Type", "Stage",
            "Days in stage", "Submit rate", "Bill rate" };
        if (showMoney) dh.AddRange(["Pay rate", "Margin / hr"]);
        dh.AddRange(["Match score", "RTR signed", "Created", "Notes"]);
        for (var i = 0; i < dh.Count; i++) ds.Cell(1, i + 1).Value = dh[i];
        StyleHeader(ds.Range(1, 1, 1, dh.Count));

        var dr = 2;
        foreach (var s in subs.OrderBy(s => s.OwnerName).ThenByDescending(s => s.CreatedUtc))
        {
            var c = 1;
            ds.Cell(dr, c++).Value = s.OwnerName;
            ds.Cell(dr, c++).Value = s.ConsultantName;
            ds.Cell(dr, c++).Value = s.ConsultantTitle;
            ds.Cell(dr, c++).Value = s.Title;
            ds.Cell(dr, c++).Value = s.Client;
            ds.Cell(dr, c++).Value = s.Vendor;
            ds.Cell(dr, c++).Value = s.Type;
            ds.Cell(dr, c++).Value = s.Stage;
            ds.Cell(dr, c++).Value = s.DaysInStage;
            ds.Cell(dr, c).Value = s.SubmitRate; ds.Cell(dr, c++).Style.NumberFormat.Format = Money;
            ds.Cell(dr, c).Value = s.BillRate; ds.Cell(dr, c++).Style.NumberFormat.Format = Money;
            if (showMoney)
            {
                ds.Cell(dr, c).Value = s.PayRate; ds.Cell(dr, c++).Style.NumberFormat.Format = Money;
                ds.Cell(dr, c).Value = s.SubmitRate - s.PayRate; ds.Cell(dr, c++).Style.NumberFormat.Format = Money;
            }
            if (s.MatchScore is int m) ds.Cell(dr, c).Value = m;
            c++;
            ds.Cell(dr, c++).Value = s.Rtr ? "Yes" : "No";
            ds.Cell(dr, c).Value = Local(s.CreatedUtc); ds.Cell(dr, c++).Style.DateFormat.Format = "mmm d, yyyy h:mm AM/PM";
            ds.Cell(dr, c++).Value = s.Notes ?? "";
            dr++;
        }
        if (subs.Count == 0) ds.Cell(2, 1).Value = "No submissions in this period.";
        else ds.Range(1, 1, dr - 1, dh.Count).SetAutoFilter();
        ds.SheetView.FreezeRows(1);
        ds.Columns(1, dh.Count).AdjustToContents(1, Math.Max(2, dr - 1));
        ds.Column(dh.Count).Width = Math.Min(ds.Column(dh.Count).Width, 60);   // keep long notes readable

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void StyleHeader(IXLRange header)
    {
        header.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(Navy);
        header.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center).Alignment.SetWrapText(true);
        header.Style.Border.SetBottomBorder(XLBorderStyleValues.Thin).Border.SetBottomBorderColor(Line);
    }
}
