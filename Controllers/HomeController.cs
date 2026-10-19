using System.Diagnostics;
using System.Globalization;
using HrProject_Budget.Data;
using HrProject_Budget.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrProject_Budget.Controllers;

public class HomeController(AppDb db, AccessService access, AuditService audit) : Controller
{
    const int DailyPointsMax = 62;      // longer periods are shown as monthly points
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // (factory, section) -> budget / allocated / absent for one date.
    // The budget always comes from the Budget Master history for that date (same rule as the report); the entry only supplies allocated / absent.
    static Dictionary<(int F, int S), (int B, int A, int Ab)> Snapshot(
        Dictionary<(int, int), int> masters, IEnumerable<DailyEntry>? entries)
    {
        var d = masters.ToDictionary(k => k.Key, k => (B: k.Value, A: 0, Ab: 0));
        if (entries != null)
            foreach (var e in entries) d[(e.FactoryId, e.SectionId)] = (masters.GetValueOrDefault((e.FactoryId, e.SectionId)), e.Allocated, e.Absent);
        return d;
    }

    public async Task<IActionResult> Index(string? mode, DateTime? date, DateTime? from, DateTime? to, string? month, int? year, int[]? f)
        => View(await BuildAsync(mode, date, from, to, month, year, f));

    /// <summary>Excel download of exactly what the dashboard is showing (same period and factories).</summary>
    public async Task<IActionResult> Export(string? mode, DateTime? date, DateTime? from, DateTime? to, string? month, int? year, int[]? f)
    {
        var vm = await BuildAsync(mode, date, from, to, month, year, f);
        if (!vm.ShowData || !vm.HasData || vm.PeriodEmpty) return RedirectToAction(nameof(Index));
        var bytes = ExcelExport.Dashboard(vm);
        await audit.LogAsync("Export", "Dashboard", null, $"Dashboard downloaded as Excel: {vm.PeriodTitle} ({(vm.AllSelected ? "all factories" : string.Join(", ", vm.Factories.Select(x => x.Name)))})");
        var tag = vm.Mode switch { "month" => vm.Month, "year" => vm.Year.ToString(), "range" => $"{vm.Start:yyyy-MM-dd}_to_{vm.End:yyyy-MM-dd}", _ => vm.End.ToString("yyyy-MM-dd") };
        return File(bytes, ExcelExport.ContentType, $"Cadre_Dashboard_{tag}.xlsx");
    }

    async Task<DashboardViewModel> BuildAsync(string? mode, DateTime? date, DateTime? from, DateTime? to, string? month, int? year, int[]? f)
    {
        var vm = new DashboardViewModel { ShowData = access.CanView("Report") };
        if (!vm.ShowData) return vm;

        var allDates = await db.Entries.Select(e => e.Date).Distinct().OrderByDescending(x => x).ToListAsync();
        vm.Dates = allDates;
        if (allDates.Count == 0) return vm;
        vm.HasData = true;

        // ----- factory selection ("customised factories"); nothing selected means all
        var allowedIds = access.AllowedFactories;
        var allFactories = await db.Factories.Where(x => allowedIds == null || allowedIds.Contains(x.Id)).OrderBy(x => x.SortOrder).ToListAsync();
        vm.AllFactories = allFactories;

        // With no date chosen, open on the latest day that EVERY factory has entered (a half-entered day would look like a collapse).
        DateTime? latestComplete = null;
        if (date == null && (mode is not ("range" or "month" or "year")))
        {
            var ids = allFactories.Select(x => x.Id).ToList();
            var complete = await db.Entries.Where(e => e.Allocated > 0 && ids.Contains(e.FactoryId))
                .GroupBy(e => e.Date).Where(g => g.Select(e => e.FactoryId).Distinct().Count() >= ids.Count).Select(g => g.Key).ToListAsync();
            if (complete.Count > 0) latestComplete = complete.Max();
        }
        var chosen = (f ?? Array.Empty<int>()).Where(id => allFactories.Any(x => x.Id == id)).ToHashSet();
        if (chosen.Count == 0) chosen = allFactories.Select(x => x.Id).ToHashSet();
        vm.SelectedIds = chosen;
        var factories = allFactories.Where(x => chosen.Contains(x.Id)).ToList();
        string FQ() => vm.AllSelected ? "" : string.Concat(chosen.OrderBy(x => x).Select(x => "&f=" + x));

        // ----- options for the month / year pickers (only periods that have data)
        vm.MonthOptions = allDates.Select(x => new DateTime(x.Year, x.Month, 1)).Distinct().OrderByDescending(x => x)
            .Select(x => (x.ToString("yyyy-MM", Inv), x.ToString("MMMM yyyy", Inv))).ToList();
        vm.YearOptions = allDates.Select(x => x.Year).Distinct().OrderByDescending(x => x).ToList();

        // ----- resolve the period
        mode = mode is "range" or "month" or "year" ? mode : "day";
        vm.Mode = mode;
        DateTime start, end;
        List<DateTime> prevDates = new();
        string prevLabel;
        switch (mode)
        {
            case "range":
            {
                start = (from ?? allDates[^1]).Date; end = (to ?? allDates[0]).Date;
                if (start > end) (start, end) = (end, start);
                var len = (end - start).Days + 1;
                var pEnd = start.AddDays(-1); var pStart = pEnd.AddDays(-(len - 1));
                prevDates = allDates.Where(x => x >= pStart && x <= pEnd).OrderBy(x => x).ToList();
                prevLabel = $"the {len} days before";
                vm.PeriodTitle = start == end ? start.ToString("dddd, dd MMMM yyyy", Inv) : $"{start:dd MMM yyyy} – {end:dd MMM yyyy}";
                break;
            }
            case "month":
            {
                var key = vm.MonthOptions.Any(o => o.Value == month) ? month! : vm.MonthOptions[0].Value;
                vm.Month = key;
                start = DateTime.ParseExact(key + "-01", "yyyy-MM-dd", Inv); end = start.AddMonths(1).AddDays(-1);
                var ps = start.AddMonths(-1); var pe = start.AddDays(-1);
                prevDates = allDates.Where(x => x >= ps && x <= pe).OrderBy(x => x).ToList();
                prevLabel = ps.ToString("MMM yyyy", Inv);
                vm.PeriodTitle = start.ToString("MMMM yyyy", Inv);
                var i = vm.MonthOptions.FindIndex(o => o.Value == key);
                if (i + 1 < vm.MonthOptions.Count) vm.PrevQuery = $"mode=month&month={vm.MonthOptions[i + 1].Value}{FQ()}";
                if (i > 0) vm.NextQuery = $"mode=month&month={vm.MonthOptions[i - 1].Value}{FQ()}";
                break;
            }
            case "year":
            {
                var y = year != null && vm.YearOptions.Contains(year.Value) ? year.Value : vm.YearOptions[0];
                vm.Year = y;
                start = new DateTime(y, 1, 1); end = new DateTime(y, 12, 31);
                prevDates = allDates.Where(x => x.Year == y - 1).OrderBy(x => x).ToList();
                prevLabel = (y - 1).ToString(Inv);
                vm.PeriodTitle = y.ToString(Inv);
                var i = vm.YearOptions.IndexOf(y);
                if (i + 1 < vm.YearOptions.Count) vm.PrevQuery = $"mode=year&year={vm.YearOptions[i + 1]}{FQ()}";
                if (i > 0) vm.NextQuery = $"mode=year&year={vm.YearOptions[i - 1]}{FQ()}";
                break;
            }
            default:
            {
                var want = date?.Date;
                var d = want != null && allDates.Contains(want.Value) ? want.Value
                      : want != null ? allDates.FirstOrDefault(x => x <= want.Value, allDates[0]) : (latestComplete ?? allDates[0]);
                if (want == null && latestComplete != null && latestComplete.Value < allDates[0])
                    vm.Notice = $"{allDates[0]:dd MMM yyyy} has entries for only some factories, so this shows the latest complete day ({latestComplete.Value:dd MMM yyyy}). Pick {allDates[0]:dd MMM} in the date box to see it anyway.";
                start = end = d;
                var pos = allDates.IndexOf(d);
                if (pos + 1 < allDates.Count) { prevDates = new() { allDates[pos + 1] }; vm.PrevQuery = $"mode=day&date={allDates[pos + 1]:yyyy-MM-dd}{FQ()}"; }
                if (pos > 0) vm.NextQuery = $"mode=day&date={allDates[pos - 1]:yyyy-MM-dd}{FQ()}";
                prevLabel = prevDates.Count > 0 ? prevDates[0].ToString("dd MMM", Inv) : "";
                vm.PeriodTitle = d.ToString("dddd, dd MMMM yyyy", Inv);
                break;
            }
        }
        vm.Start = start; vm.End = end; vm.PrevLabel = prevLabel;
        var dates = allDates.Where(x => x >= start && x <= end).OrderBy(x => x).ToList();
        vm.DaysCount = dates.Count;
        if (dates.Count == 0) { vm.PeriodEmpty = true; return vm; }
        // A short period (a day, or a few days) shows the lead-up days on the trend chart so it is never a lone dot.
        var useContext = (mode == "day" || mode == "range") && dates.Count < 7;
        var trendDates = useContext ? allDates.Where(x => x <= end).Take(30).OrderBy(x => x).ToList() : dates;
        vm.TrendNote = useContext && trendDates.Count > dates.Count ? $"last {trendDates.Count} days up to {end:dd MMM yyyy}" : "";
        vm.PeriodNote = dates.Count == 1 ? "" : $"Average per day over {dates.Count} day{(dates.Count == 1 ? "" : "s")} with data";

        // ----- load what we need
        var lo = dates.Concat(prevDates).Concat(trendDates).Min(); var hi = dates.Concat(prevDates).Concat(trendDates).Max();
        var wanted = dates.Concat(prevDates).Concat(trendDates).ToHashSet();
        var entries = (await db.Entries.Where(e => e.Date >= lo && e.Date <= hi).ToListAsync())
            .Where(e => wanted.Contains(e.Date)).GroupBy(e => e.Date).ToDictionary(g => g.Key, g => g.ToList());
        var rates = await BudgetRates.LoadAsync(db);
        var sections = await db.Sections.Include(s => s.Department).OrderBy(s => s.Department!.SortOrder).ThenBy(s => s.SortOrder).ToListAsync();
        var secInfo = sections.ToDictionary(s => s.Id);
        bool IsMain(int sectionId) => !secInfo[sectionId].Department!.SeparateTotal;
        var snaps = wanted.ToDictionary(x => x, x => Snapshot(rates.At(x), entries.GetValueOrDefault(x)));

        // average per day over a set of dates, for the selected factories and any extra filter
        Totals Agg(IEnumerable<DateTime> ds, Func<int, int, bool> where)
        {
            long b = 0, a = 0, ab = 0; int n = 0;
            foreach (var x in ds)
            {
                n++;
                foreach (var (k, v) in snaps[x])
                    if (chosen.Contains(k.F) && secInfo.ContainsKey(k.S) && where(k.F, k.S)) { b += v.B; a += v.A; ab += v.Ab; }
            }
            var t = new Totals();
            if (n > 0) t.Add((int)Math.Round(b / (double)n), (int)Math.Round(a / (double)n), (int)Math.Round(ab / (double)n));
            return t;
        }

        // ----- trend points: days, or months when the period is long
        var buckets = trendDates.Count > DailyPointsMax || mode == "year"
            ? trendDates.GroupBy(x => new DateTime(x.Year, x.Month, 1)).OrderBy(g => g.Key).Select(g => (Label: g.Key.ToString("MMM yyyy", Inv), Dates: g.ToList())).ToList()
            : trendDates.Select(x => (Label: x.ToString("dd MMM", Inv), Dates: new List<DateTime> { x })).ToList();
        vm.TrendUnit = trendDates.Count > DailyPointsMax || mode == "year" ? "month" : "day";
        foreach (var bk in buckets) vm.Trend.Add(new TrendPoint { Label = bk.Label, T = Agg(bk.Dates, (fid, s) => IsMain(s)) });

        vm.Today = Agg(dates, (fid, s) => IsMain(s));
        if (prevDates.Count > 0) vm.Prev = Agg(prevDates, (fid, s) => IsMain(s));

        var sepNames = sections.Where(s => s.Department!.SeparateTotal).Select(s => s.Department!.Name).Distinct().ToList();
        vm.SeparateNote = sepNames.Count == 0 ? "" : string.Join(" and ", sepNames) + " have their own totals and are not included in the comparison figures.";

        // ----- section level averages for the period (main departments)
        var fname = allFactories.ToDictionary(x => x.Id, x => x.Name);
        var acc = new Dictionary<(int F, int S), (long B, long A, long Ab)>();
        foreach (var x in dates)
            foreach (var (k, v) in snaps[x])
            {
                if (!chosen.Contains(k.F) || !secInfo.ContainsKey(k.S) || !IsMain(k.S)) continue;
                var c = acc.GetValueOrDefault(k); acc[k] = (c.B + v.B, c.A + v.A, c.Ab + v.Ab);
            }
        var items = new List<WatchItem>();
        foreach (var (k, c) in acc)
        {
            var t = new Totals(); int n = dates.Count;
            t.Add((int)Math.Round(c.B / (double)n), (int)Math.Round(c.A / (double)n), (int)Math.Round(c.Ab / (double)n));
            if (t.Budget == 0 && t.Allocated == 0) continue;
            items.Add(new WatchItem { Factory = fname[k.F], Department = secInfo[k.S].Department!.Name, Section = secInfo[k.S].Name, T = t });
        }

        // ----- one entry per selected factory
        foreach (var fac in factories)
        {
            var fs = new FactoryStat
            {
                Id = fac.Id, Name = fac.Name, FullName = fac.FullName ?? fac.Name,
                T = Agg(dates, (fid, s) => fid == fac.Id && IsMain(s)),
                Prev = prevDates.Count > 0 ? Agg(prevDates, (fid, s) => fid == fac.Id && IsMain(s)) : new Totals()
            };
            foreach (var bk in buckets) fs.History.Add(Agg(bk.Dates, (fid, s) => fid == fac.Id && IsMain(s)));
            var mine = items.Where(i => i.Factory == fac.Name).ToList();
            fs.TopShort = mine.Where(i => i.T.Shortage < 0).OrderBy(i => i.T.Shortage).Take(3).ToList();
            fs.TopAbsent = mine.Where(i => i.T.Allocated >= 10 && i.T.Absent > 0).OrderByDescending(i => i.T.AbsentPct).ThenByDescending(i => i.T.Absent).Take(3).ToList();
            vm.Factories.Add(fs);
        }

        // ----- department x factory heat map (all departments, separate ones flagged)
        foreach (var g in sections.GroupBy(s => s.Department!))
        {
            var ids = g.Select(s => s.Id).ToHashSet();
            var row = new HeatRow { Department = g.Key.Name, Separate = g.Key.SeparateTotal };
            foreach (var fac in factories) row.Cells.Add(Agg(dates, (fid, s) => fid == fac.Id && ids.Contains(s)));
            row.Total = Agg(dates, (fid, s) => ids.Contains(s));
            if (row.Total.Budget > 0 || row.Total.Allocated > 0) vm.Heat.Add(row);
        }

        vm.GrossShortage = items.Where(i => i.T.Shortage < 0).Sum(i => -i.T.Shortage);
        vm.GrossSurplus = items.Where(i => i.T.Shortage > 0).Sum(i => i.T.Shortage);
        vm.RebalanceWithinFactory = items.GroupBy(i => i.Factory)
            .Sum(g => Math.Min(g.Where(i => i.T.Shortage < 0).Sum(i => -i.T.Shortage), g.Where(i => i.T.Shortage > 0).Sum(i => i.T.Shortage)));
        return vm;
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
