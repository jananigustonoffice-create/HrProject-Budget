using System.ComponentModel.DataAnnotations;

namespace HrProject_Budget.Models;

public class Factory
{
    public int Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(200)] public string? FullName { get; set; }
    public int SortOrder { get; set; }
}

public class Department
{
    public int Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    /// <summary>Totalled on its own (own Grand Total) instead of rolling into the main Grand Total, e.g. Training Section.</summary>
    [Display(Name = "Separate grand total")] public bool SeparateTotal { get; set; }
}

public class Section
{
    public int Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Display(Name = "Department")] public int DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Budgeted cadre for a section in a factory.</summary>
public class BudgetMaster
{
    public int Id { get; set; }
    [Display(Name = "Factory")] public int FactoryId { get; set; }
    public Factory? Factory { get; set; }
    [Display(Name = "Section")] public int SectionId { get; set; }
    public Section? Section { get; set; }
    [Range(0, 100000)] public int Budget { get; set; }
}

/// <summary>Daily allocated / absent entry. Present and Shortage are derived.</summary>
public class DailyEntry
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public int FactoryId { get; set; }
    public Factory? Factory { get; set; }
    public int SectionId { get; set; }
    public Section? Section { get; set; }
    /// <summary>Budget that applied on this date (defaults from Budget Master, may change day to day).</summary>
    public int Budget { get; set; }
    public int Allocated { get; set; }
    public int Absent { get; set; }
}

public class EntryRow
{
    public int SectionId { get; set; }
    public string Department { get; set; } = "";
    public string Section { get; set; } = "";
    public int Budget { get; set; }
    public int Allocated { get; set; }
    public int Absent { get; set; }
}

public class EntryViewModel
{
    public DateTime Date { get; set; } = DateTime.Today;
    public int FactoryId { get; set; }
    public List<Factory> Factories { get; set; } = new();
    public List<EntryRow> Rows { get; set; } = new();
}
public class ReportCell
{
    public int Budget { get; set; }
    public int Allocated { get; set; }
    public int Absent { get; set; }
    public int Present => Allocated - Absent;
    public int Shortage => Allocated - Budget;
}

public class ReportRow
{
    public string Department { get; set; } = "";
    public string Section { get; set; } = "";
    public bool Separate { get; set; }
    public Dictionary<int, ReportCell> Cells { get; set; } = new();
}

public class ReportViewModel
{
    public DateTime Date { get; set; }
    public List<Factory> Factories { get; set; } = new();
    public List<ReportRow> Rows { get; set; } = new();
}

public class TotalRowModel
{
    public string Label { get; set; } = "";
    public string CssClass { get; set; } = "";
    public List<ReportRow> Rows { get; set; } = new();
    public List<Factory> Factories { get; set; } = new();
}

public class ImportResult
{
    public int Days { get; set; }
    public int Rows { get; set; }
    public List<string> Dates { get; } = new();
    public List<string> Warnings { get; } = new();
}



