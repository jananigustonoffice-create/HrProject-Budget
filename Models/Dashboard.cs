namespace HrProject_Budget.Models;

/// <summary>
/// Cadre figures. For a single day these are that day's numbers; for a longer period they are the
/// average per day (cadre is a headcount, so adding days together would overstate it).
/// </summary>
public class Totals
{
    public int Budget { get; set; }
    public int Allocated { get; set; }
    public int Absent { get; set; }
    public int Present => Allocated - Absent;
    public int Shortage => Allocated - Budget;                       // negative = short
    public double Fulfilment => Budget <= 0 ? 0 : Allocated * 100.0 / Budget;
    public double OnFloor => Budget <= 0 ? 0 : Present * 100.0 / Budget;
    public double AbsentPct => Allocated <= 0 ? 0 : Absent * 100.0 / Allocated;
    public void Add(int budget, int allocated, int absent) { Budget += budget; Allocated += allocated; Absent += absent; }
}

/// <summary>One point on a trend line: a day, or a month when the period is long.</summary>
public class TrendPoint { public string Label { get; set; } = ""; public Totals T { get; set; } = new(); }

public class FactoryStat
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public Totals T { get; set; } = new();
    public Totals Prev { get; set; } = new();
    public List<Totals> History { get; set; } = new();        // one per trend point
    public List<WatchItem> TopShort { get; set; } = new();     // biggest shortages in this factory
    public List<WatchItem> TopAbsent { get; set; } = new();    // highest absenteeism in this factory
}

public class HeatRow
{
    public string Department { get; set; } = "";
    public bool Separate { get; set; }
    public List<Totals> Cells { get; set; } = new();   // one per selected factory, same order as Factories
    public Totals Total { get; set; } = new();
}

public class WatchItem
{
    public string Factory { get; set; } = "";
    public string Department { get; set; } = "";
    public string Section { get; set; } = "";
    public Totals T { get; set; } = new();
}

public class DashboardViewModel
{
    public bool ShowData { get; set; }
    public bool HasData { get; set; }            // any entries at all
    public bool PeriodEmpty { get; set; }        // the chosen period has no entries

    // ---- filter state
    public string Mode { get; set; } = "day";    // day | range | month | year
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Month { get; set; } = "";      // yyyy-MM
    public int Year { get; set; }
    public List<DateTime> Dates { get; set; } = new();                   // dates that have data, newest first
    public List<(string Value, string Label)> MonthOptions { get; set; } = new();
    public List<int> YearOptions { get; set; } = new();
    public List<Factory> AllFactories { get; set; } = new();
    public HashSet<int> SelectedIds { get; set; } = new();
    public bool AllSelected => SelectedIds.Count == AllFactories.Count;
    public string? PrevQuery { get; set; }       // query string for the previous day / month / year
    public string? NextQuery { get; set; }

    // ---- what is being shown
    public string PeriodTitle { get; set; } = "";    // e.g. "Monday, 12 October 2026" or "October 2026"
    public string PeriodNote { get; set; } = "";     // e.g. "Average per day over 10 days with data"
    public int DaysCount { get; set; }
    public bool IsAverage => DaysCount > 1;
    public string PrevLabel { get; set; } = "";      // what deltas compare against
    public string TrendUnit { get; set; } = "day";   // day | month
    public string TrendNote { get; set; } = "";      // e.g. "last 14 days up to 12 Oct 2026" when lead-up days are shown

    public Totals Today { get; set; } = new();
    public Totals? Prev { get; set; }
    public List<TrendPoint> Trend { get; set; } = new();     // oldest first
    public List<FactoryStat> Factories { get; set; } = new(); // selected factories only
    public List<HeatRow> Heat { get; set; } = new();
    public int GrossShortage { get; set; }
    public int GrossSurplus { get; set; }
    public int RebalanceWithinFactory { get; set; }
    public string SeparateNote { get; set; } = "";
    public string Notice { get; set; } = "";         // e.g. why the page opened on an earlier day
}
