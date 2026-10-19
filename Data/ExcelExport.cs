using System.Text.Json;
using ClosedXML.Excel;
using HrProject_Budget.Models;

namespace HrProject_Budget.Data;

/// <summary>Builds the .xlsx downloads (report, dashboard, audit log).</summary>
public static class ExcelExport
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    static readonly string[] FactoryColors = { "#2A78D6", "#EB6834", "#1BAF7A", "#EDA100", "#E87BA4" };
    const string Ink = "#0F172A", Band = "#E6F7F5", BandInk = "#0F766E", Dark = "#15171C", Soft = "#F8FAFC", Red = "#D03B3B";

    static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    static void Title(IXLWorksheet ws, string title, string sub)
    {
        ws.Cell(1, 1).Value = title; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 15; ws.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml(Ink);
        ws.Cell(2, 1).Value = sub; ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#64748B");
    }

    static void Header(IXLRange r, string fill, string font = "#FFFFFF")
    {
        r.Style.Fill.BackgroundColor = XLColor.FromHtml(fill); r.Style.Font.FontColor = XLColor.FromHtml(font); r.Style.Font.Bold = true;
        r.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; r.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    static void Money(IXLRange r) => r.Style.NumberFormat.Format = "#,##0;[Red]-#,##0;0";

    // ============================================================ Report (one sheet, like the Excel layout)
    public static byte[] Report(ReportViewModel vm)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Cadre report");
        int F = vm.Factories.Count, last = 1 + 5 * (F + 1);
        Title(ws, "Concord Group – Budgeted & Actual MO/TMO Cadre", $"Date: {vm.Date:dddd, dd MMMM yyyy}");

        ws.Cell(4, 1).Value = "Section"; ws.Range(4, 1, 5, 1).Merge(); Header(ws.Range(4, 1, 5, 1), Dark);
        for (int i = 0; i <= F; i++)
        {
            int c = 2 + i * 5;
            var rng = ws.Range(4, c, 4, c + 4).Merge();
            ws.Cell(4, c).Value = i < F ? vm.Factories[i].FullName ?? vm.Factories[i].Name : "Group Total";
            Header(rng, i < F ? FactoryColors[i % FactoryColors.Length] : Dark);
            var names = new[] { "Budget", "Allocated", "Ab", "Present", "Shortage" };
            for (int k = 0; k < 5; k++) ws.Cell(5, c + k).Value = names[k];
            Header(ws.Range(5, c, 5, c + 4), "#F1F4FB", "#475569");
        }

        ReportCell Sum(IEnumerable<ReportRow> rs, int? fid)
        {
            var cells = rs.SelectMany(r => r.Cells.Where(k => fid == null ? vm.Factories.Any(f => f.Id == k.Key) : k.Key == fid)).Select(k => k.Value).ToList();
            return new ReportCell { Budget = cells.Sum(x => x.Budget), Allocated = cells.Sum(x => x.Allocated), Absent = cells.Sum(x => x.Absent) };
        }

        int row = 6;
        void Line(string label, Func<int?, ReportCell> get, string kind)
        {
            ws.Cell(row, 1).Value = label;
            for (int i = 0; i <= F; i++)
            {
                var cell = get(i < F ? vm.Factories[i].Id : null);
                int c = 2 + i * 5;
                ws.Cell(row, c).Value = cell.Budget; ws.Cell(row, c + 1).Value = cell.Allocated; ws.Cell(row, c + 2).Value = cell.Absent;
                ws.Cell(row, c + 3).Value = cell.Present; ws.Cell(row, c + 4).Value = cell.Shortage;
            }
            var all = ws.Range(row, 1, row, last);
            Money(ws.Range(row, 2, row, last));
            if (kind == "sub") { all.Style.Font.Bold = true; all.Style.Fill.BackgroundColor = XLColor.FromHtml(Soft); all.Style.Border.TopBorder = XLBorderStyleValues.Thin; }
            if (kind == "grand") { all.Style.Font.Bold = true; all.Style.Fill.BackgroundColor = XLColor.FromHtml(Dark); all.Style.Font.FontColor = XLColor.White; all.Style.NumberFormat.Format = "#,##0;-#,##0;0"; }
            row++;
        }
        void BandRow(string text)
        {
            var r = ws.Range(row, 1, row, last).Merge(); ws.Cell(row, 1).Value = text.ToUpperInvariant();
            r.Style.Fill.BackgroundColor = XLColor.FromHtml(Band); r.Style.Font.FontColor = XLColor.FromHtml(BandInk); r.Style.Font.Bold = true; row++;
        }

        var main = vm.Rows.Where(r => !r.Separate).ToList();
        foreach (var g in main.GroupBy(r => r.Department))
        {
            BandRow(g.Key);
            foreach (var r in g) Line(r.Section, fid => Sum(new[] { r }, fid), "row");
            Line("Sub Total", fid => Sum(g, fid), "sub");
        }
        if (main.Count > 0) Line("Grand Total (" + string.Join(" / ", main.Select(r => r.Department).Distinct()) + ")", fid => Sum(main, fid), "grand");
        foreach (var g in vm.Rows.Where(r => r.Separate).GroupBy(r => r.Department))
        {
            row++;
            BandRow(g.Key);
            foreach (var r in g) Line(r.Section, fid => Sum(new[] { r }, fid), "row");
            Line("Grand Total (" + g.Key + ")", fid => Sum(g, fid), "grand");
        }

        ws.Column(1).Width = 34; for (int c = 2; c <= last; c++) ws.Column(c).Width = 10.5;
        ws.Range(4, 1, row - 1, last).Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        ws.SheetView.FreezeRows(5); ws.SheetView.FreezeColumns(1);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape; ws.PageSetup.FitToPages(1, 0);
        return Save(wb);
    }

    // ============================================================ Dashboard (summary, trend, departments, rankings)
    public static byte[] Dashboard(DashboardViewModel vm)
    {
        using var wb = new XLWorkbook();
        var T = vm.Today;
        var fnames = vm.Factories.Select(f => f.Name).ToList();
        var sub = $"{vm.PeriodTitle}{(vm.IsAverage ? $"  ·  average per day over {vm.DaysCount} days with data" : "")}  ·  Factories: {(vm.AllSelected ? "all" : string.Join(", ", fnames))}";

        // ---- Summary
        var ws = wb.AddWorksheet("Summary");
        Title(ws, "Concord Group – Factory comparison dashboard", sub);
        ws.Cell(4, 1).Value = "Group figures"; ws.Cell(4, 1).Style.Font.Bold = true;
        var heads = new[] { "Measure", "Value", vm.Prev == null ? "Previous" : $"vs {vm.PrevLabel}", "Change" };
        for (int k = 0; k < heads.Length; k++) ws.Cell(5, 1 + k).Value = heads[k];
        Header(ws.Range(5, 1, 5, 4), Dark);
        var P = vm.Prev;
        var figs = new (string L, double V, double? Pv, string F)[]
        {
            ("Budget", T.Budget, P?.Budget, "#,##0"), ("Allocated", T.Allocated, P?.Allocated, "#,##0"), ("Present", T.Present, P?.Present, "#,##0"),
            ("Shortage / surplus", T.Shortage, P?.Shortage, "#,##0;[Red]-#,##0"), ("Fulfilment %", Math.Round(T.Fulfilment, 1), P == null ? null : Math.Round(P.Fulfilment, 1), "0.0"),
            ("Absenteeism %", Math.Round(T.AbsentPct, 1), P == null ? null : Math.Round(P.AbsentPct, 1), "0.0")
        };
        int r0 = 6;
        foreach (var (l, v, pv, f) in figs)
        {
            ws.Cell(r0, 1).Value = l; ws.Cell(r0, 2).Value = v;
            if (pv != null) { ws.Cell(r0, 3).Value = pv.Value; ws.Cell(r0, 4).Value = Math.Round(v - pv.Value, 1); }
            ws.Range(r0, 2, r0, 4).Style.NumberFormat.Format = f; r0++;
        }
        ws.Cell(r0, 1).Value = "Figures are the average per day when the period is longer than one day."; ws.Cell(r0, 1).Style.Font.FontColor = XLColor.FromHtml("#64748B"); r0 += 2;

        ws.Cell(r0, 1).Value = "Factory scoreboard"; ws.Cell(r0, 1).Style.Font.Bold = true; r0++;
        ws.Cell(r0, 1).Value = "Measure"; Header(ws.Cell(r0, 1).AsRange(), Dark);
        for (int i = 0; i < vm.Factories.Count; i++) { ws.Cell(r0, 2 + i).Value = vm.Factories[i].Name; Header(ws.Cell(r0, 2 + i).AsRange(), FactoryColors[(vm.AllFactories.FindIndex(x => x.Id == vm.Factories[i].Id) % FactoryColors.Length + FactoryColors.Length) % FactoryColors.Length]); }
        ws.Cell(r0, 2 + vm.Factories.Count).Value = vm.AllSelected ? "Group" : "Selected"; Header(ws.Cell(r0, 2 + vm.Factories.Count).AsRange(), Dark);
        r0++;
        var board = new (string L, Func<Totals, double> G, string F)[]
        {
            ("Budget", x => x.Budget, "#,##0"), ("Allocated", x => x.Allocated, "#,##0"), ("Present on floor", x => x.Present, "#,##0"),
            ("Shortage / surplus", x => x.Shortage, "#,##0;[Red]-#,##0"), ("Fulfilment %", x => Math.Round(x.Fulfilment, 1), "0.0"),
            ("On-floor rate %", x => Math.Round(x.OnFloor, 1), "0.0"), ("Absenteeism %", x => Math.Round(x.AbsentPct, 1), "0.0")
        };
        foreach (var (l, g, f) in board)
        {
            ws.Cell(r0, 1).Value = l;
            for (int i = 0; i < vm.Factories.Count; i++) ws.Cell(r0, 2 + i).Value = g(vm.Factories[i].T);
            ws.Cell(r0, 2 + vm.Factories.Count).Value = g(T);
            ws.Range(r0, 2, r0, 2 + vm.Factories.Count).Style.NumberFormat.Format = f; r0++;
        }
        ws.Column(1).Width = 26; for (int c = 2; c <= 8; c++) ws.Column(c).Width = 18;

        // ---- Trend
        var wt = wb.AddWorksheet("Trend");
        Title(wt, "Fulfilment trend", sub);
        wt.Cell(4, 1).Value = vm.TrendUnit == "month" ? "Month" : "Date";
        for (int i = 0; i < vm.Factories.Count; i++) wt.Cell(4, 2 + i).Value = vm.Factories[i].Name + " fulfilment %";
        int gc = 2 + vm.Factories.Count;
        string[] gh = { "Group fulfilment %", "Budget", "Allocated", "Present", "Absent %" };
        for (int k = 0; k < gh.Length; k++) wt.Cell(4, gc + k).Value = gh[k];
        Header(wt.Range(4, 1, 4, gc + gh.Length - 1), Dark);
        for (int r = 0; r < vm.Trend.Count; r++)
        {
            wt.Cell(5 + r, 1).Value = vm.Trend[r].Label;
            for (int i = 0; i < vm.Factories.Count; i++) wt.Cell(5 + r, 2 + i).Value = Math.Round(vm.Factories[i].History[r].Fulfilment, 1);
            var t = vm.Trend[r].T;
            wt.Cell(5 + r, gc).Value = Math.Round(t.Fulfilment, 1); wt.Cell(5 + r, gc + 1).Value = t.Budget; wt.Cell(5 + r, gc + 2).Value = t.Allocated;
            wt.Cell(5 + r, gc + 3).Value = t.Present; wt.Cell(5 + r, gc + 4).Value = Math.Round(t.AbsentPct, 1);
        }
        wt.Range(5, 2, 4 + vm.Trend.Count, gc + 4).Style.NumberFormat.Format = "#,##0.0";
        wt.Range(5, gc + 1, 4 + vm.Trend.Count, gc + 3).Style.NumberFormat.Format = "#,##0";
        wt.Columns().AdjustToContents(); wt.SheetView.FreezeRows(4);

        // ---- Department x factory
        var wd = wb.AddWorksheet("Departments");
        Title(wd, "Department by factory", sub);
        wd.Cell(4, 1).Value = "Department"; wd.Range(4, 1, 5, 1).Merge(); Header(wd.Range(4, 1, 5, 1), Dark);
        for (int i = 0; i <= vm.Factories.Count; i++)
        {
            int c = 2 + i * 3;
            wd.Range(4, c, 4, c + 2).Merge(); wd.Cell(4, c).Value = i < vm.Factories.Count ? vm.Factories[i].Name : (vm.AllSelected ? "Group" : "Selected");
            Header(wd.Range(4, c, 4, c + 2), i < vm.Factories.Count ? FactoryColors[(vm.AllFactories.FindIndex(x => x.Id == vm.Factories[i].Id) % FactoryColors.Length + FactoryColors.Length) % FactoryColors.Length] : Dark);
            wd.Cell(5, c).Value = "Budget"; wd.Cell(5, c + 1).Value = "Allocated"; wd.Cell(5, c + 2).Value = "Shortage";
            Header(wd.Range(5, c, 5, c + 2), "#F1F4FB", "#475569");
        }
        int dr = 6;
        foreach (var h in vm.Heat)
        {
            wd.Cell(dr, 1).Value = h.Department + (h.Separate ? " (own total)" : "");
            for (int i = 0; i <= vm.Factories.Count; i++)
            {
                var c = i < vm.Factories.Count ? h.Cells[i] : h.Total; int col = 2 + i * 3;
                wd.Cell(dr, col).Value = c.Budget; wd.Cell(dr, col + 1).Value = c.Allocated; wd.Cell(dr, col + 2).Value = c.Shortage;
            }
            dr++;
        }
        Money(wd.Range(6, 2, dr - 1, 1 + 3 * (vm.Factories.Count + 1)));
        wd.Column(1).Width = 36; for (int c = 2; c <= 1 + 3 * (vm.Factories.Count + 1); c++) wd.Column(c).Width = 11;

        // ---- Rankings
        void Rank(string sheet, string title, Func<FactoryStat, List<WatchItem>> pick, string[] cols, Func<WatchItem, object[]> cells)
        {
            var w = wb.AddWorksheet(sheet); Title(w, title, sub);
            var head = new[] { "Factory", "Rank", "Section", "Department" }.Concat(cols).ToArray();
            for (int k = 0; k < head.Length; k++) w.Cell(4, 1 + k).Value = head[k];
            Header(w.Range(4, 1, 4, head.Length), Dark);
            int rr = 5;
            foreach (var f in vm.Factories)
            {
                int n = 1;
                foreach (var it in pick(f))
                {
                    w.Cell(rr, 1).Value = f.Name; w.Cell(rr, 2).Value = n++; w.Cell(rr, 3).Value = it.Section; w.Cell(rr, 4).Value = it.Department;
                    var vals = cells(it); for (int k = 0; k < vals.Length; k++) w.Cell(rr, 5 + k).Value = XLCellValue.FromObject(vals[k]);
                    rr++;
                }
            }
            w.Columns().AdjustToContents();
        }
        Rank("Top shortages", "Biggest shortages – top 3 sections per factory", f => f.TopShort, new[] { "Budget", "Allocated", "Shortage" },
            i => new object[] { i.T.Budget, i.T.Allocated, i.T.Shortage });
        Rank("Top absenteeism", "Highest absenteeism – top 3 sections per factory (10+ allocated)", f => f.TopAbsent, new[] { "Allocated", "Absent", "Absent %" },
            i => new object[] { i.T.Allocated, i.T.Absent, Math.Round(i.T.AbsentPct, 1) });

        return Save(wb);
    }

    // ============================================================ Audit log
    public static byte[] Audit(List<AuditLog> rows, string filterText)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Audit log");
        Title(ws, "Concord Group – Activity log", filterText);
        var head = new[] { "Time", "User", "Action", "Entity", "Entity id", "Summary", "Details (old → new)", "IP address" };
        for (int k = 0; k < head.Length; k++) ws.Cell(4, 1 + k).Value = head[k];
        Header(ws.Range(4, 1, 4, head.Length), Dark);
        int r = 5;
        foreach (var a in rows)
        {
            ws.Cell(r, 1).Value = a.Time.ToLocalTime(); ws.Cell(r, 1).Style.DateFormat.Format = "dd-mmm-yyyy hh:mm:ss";
            ws.Cell(r, 2).Value = a.Username; ws.Cell(r, 3).Value = a.Action; ws.Cell(r, 4).Value = a.Entity; ws.Cell(r, 5).Value = a.EntityId ?? "";
            ws.Cell(r, 6).Value = a.Summary; ws.Cell(r, 7).Value = Flatten(a.Details); ws.Cell(r, 8).Value = a.Ip ?? "";
            r++;
        }
        ws.Column(1).Width = 21; ws.Column(2).Width = 16; ws.Column(3).Width = 13; ws.Column(4).Width = 16; ws.Column(5).Width = 14; ws.Column(6).Width = 70; ws.Column(7).Width = 80; ws.Column(8).Width = 16;
        ws.Range(5, 6, Math.Max(5, r - 1), 7).Style.Alignment.WrapText = true;
        ws.Range(5, 1, Math.Max(5, r - 1), 8).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        if (r > 5) ws.Range(4, 1, r - 1, 8).SetAutoFilter();
        ws.SheetView.FreezeRows(4);
        return Save(wb);
    }

    public static string Flatten(string? details)
    {
        if (string.IsNullOrEmpty(details)) return "";
        try
        {
            var list = JsonSerializer.Deserialize<List<AuditChange>>(details) ?? new();
            return string.Join("\n", list.Select(c => c.Old == null ? $"{c.Field}: {c.New}" : c.New == null ? $"{c.Field}: {c.Old} (removed)" : $"{c.Field}: {c.Old} → {c.New}"));
        }
        catch { return details; }
    }
}
