using HrProject_Budget.Data;
using HrProject_Budget.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrProject_Budget.Controllers;

public class EntryController(AppDb db, AccessService access, AuditService audit) : Controller
{
    async Task<EntryViewModel> Build(DateTime date, int factoryId)
    {
        var allowed = access.AllowedFactories;
        if (factoryId != 0 && !access.CanSeeFactory(factoryId)) factoryId = 0;
        var vm = new EntryViewModel
        {
            Date = date.Date, FactoryId = factoryId,
            Factories = await db.Factories.Where(f => allowed == null || allowed.Contains(f.Id)).OrderBy(f => f.SortOrder).ToListAsync()
        };
        if (factoryId == 0) return vm;
        var rates = await BudgetRates.LoadAsync(db, factoryId);
        var entries = await db.Entries.Where(e => e.FactoryId == factoryId && e.Date == vm.Date).ToDictionaryAsync(e => e.SectionId);

        var sections = await db.Sections.Include(s => s.Department).OrderBy(s => s.Department!.SortOrder).ThenBy(s => s.SortOrder).ToListAsync();
        // Every section of every department is listed, so cadre can be entered even where no budget is set.
        foreach (var s in sections)
        {
            entries.TryGetValue(s.Id, out var e);
            vm.Rows.Add(new EntryRow
            {
                SectionId = s.Id, Department = s.Department!.Name, Section = s.Name,
                Budget = rates.Get(factoryId, s.Id, vm.Date),
                Allocated = e?.Allocated ?? 0,
                Absent = e?.Absent ?? 0
            });
        }
        return vm;
    }

    public async Task<IActionResult> Index(DateTime? date, int factoryId = 0) => View(await Build(date ?? DateTime.Today, factoryId));

    [HttpPost]
    public async Task<IActionResult> Index(EntryViewModel m)
    {
        if (!access.CanSeeFactory(m.FactoryId)) return RedirectToAction("AccessDenied", "Account");
        var date = m.Date.Date;
        foreach (var r in m.Rows)
        {
            if (r.Allocated < 0 || r.Absent < 0 || r.Budget < 0) { TempData["Error"] = $"{r.Section}: values cannot be negative."; return View(await Build(date, m.FactoryId)); }
            if (r.Absent > r.Allocated) { TempData["Error"] = $"{r.Section}: absent cannot exceed allocated."; return View(await Build(date, m.FactoryId)); }
        }
        var rates = await BudgetRates.LoadAsync(db, m.FactoryId);
        var existing = await db.Entries.Where(e => e.FactoryId == m.FactoryId && e.Date == date).ToDictionaryAsync(e => e.SectionId);
        foreach (var r in m.Rows)
        {
            // The posted budget is ignored: budgets are only changed in Budget Master.
            var bud = rates.Get(m.FactoryId, r.SectionId, date);
            if (existing.TryGetValue(r.SectionId, out var e)) { e.Budget = bud; e.Allocated = r.Allocated; e.Absent = r.Absent; }
            else if (r.Allocated > 0 || r.Absent > 0)
                db.Entries.Add(new DailyEntry { Date = date, FactoryId = m.FactoryId, SectionId = r.SectionId, Budget = bud, Allocated = r.Allocated, Absent = r.Absent });
        }
        await db.SaveChangesAsync();
        TempData["Ok"] = "Saved.";
        return RedirectToAction(nameof(Index), new { date = date.ToString("yyyy-MM-dd"), factoryId = m.FactoryId });
    }
    public IActionResult Import() => View();

    [HttpPost, RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Import(IFormFile? file)
    {
        if (file == null || file.Length == 0) { TempData["Error"] = "Choose an .xlsx file."; return View(); }
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) { TempData["Error"] = "Only .xlsx files are supported."; return View(); }

        List<ParsedSheet> sheets;
        try { await using var s = file.OpenReadStream(); sheets = ExcelReader.Read(s); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or KeyNotFoundException or FormatException)
        { TempData["Error"] = "Could not read the workbook: " + ex.Message; return View(); }

        var result = new ImportResult();
        var skippedNoAccess = 0;
        var factories = await db.Factories.ToListAsync();
        var rates = await BudgetRates.LoadAsync(db);
        var departments = await db.Departments.ToListAsync();
        var sections = await db.Sections.ToListAsync();

        foreach (var sheet in sheets.Where(s => s.Date != null).OrderBy(s => s.Date))
        {
            var date = sheet.Date!.Value;
            // Map each 5-column group to a factory by its heading, e.g. "Concord, Avissawella" contains "Avissawella".
            var groupFactory = new Dictionary<int, Factory>();
            foreach (var (g, text) in sheet.GroupHeaders)
            {
                var f = factories.OrderByDescending(x => x.Name.Length).FirstOrDefault(x => text.Contains(x.Name, StringComparison.OrdinalIgnoreCase));
                if (f != null) groupFactory[g] = f;
                else result.Warnings.Add($"{date:dd MMM}: no factory matches heading '{text}' - skipped.");
            }

            var existing = await db.Entries.Where(e => e.Date == date).ToListAsync();
            var imported = 0;
            foreach (var row in sheet.Rows)
            {
                if (!groupFactory.TryGetValue(row.Group, out var factory)) continue;
                if (!access.CanSeeFactory(factory.Id)) { skippedNoAccess++; continue; }   // not one of this user's factories
                var dept = departments.OrderByDescending(d => d.Name.Length).FirstOrDefault(d => row.DepartmentHeader.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
                var section = dept == null ? null : sections.FirstOrDefault(s => s.DepartmentId == dept.Id && s.Name.Equals(row.Section, StringComparison.OrdinalIgnoreCase));
                if (section == null)
                {
                    var w = $"No section '{row.Section}' in department '{row.DepartmentHeader}' - add it to the masters and import again.";
                    if (!result.Warnings.Contains(w)) result.Warnings.Add(w);
                    continue;
                }
                var e = existing.FirstOrDefault(x => x.FactoryId == factory.Id && x.SectionId == section.Id);
                if (e == null)
                {
                    e = new DailyEntry { Date = date, FactoryId = factory.Id, SectionId = section.Id };
                    db.Entries.Add(e);
                    existing.Add(e);
                }
                e.Budget = rates.Get(factory.Id, section.Id, date);   // the sheet's budget is ignored; Budget Master decides
                e.Allocated = row.Allocated; e.Absent = Math.Min(row.Absent, row.Allocated);
                imported++;
            }
            result.Days++; result.Rows += imported; result.Dates.Add(date.ToString("dd MMM yyyy"));
        }
        foreach (var s in sheets.Where(s => s.Date == null)) result.Warnings.Add($"Sheet '{s.Name}' has no date in cell B3 - skipped.");

        if (skippedNoAccess > 0) result.Warnings.Add($"{skippedNoAccess} row(s) for factories you do not have access to were skipped.");

        db.SuppressEntryAudit = true;                       // one summary line below instead of one per day and factory
        await db.SaveChangesAsync();
        db.SuppressEntryAudit = false;
        var changes = new List<AuditChange> { new() { Field = "File", New = file.FileName }, new() { Field = "Days imported", New = string.Join(", ", result.Dates) }, new() { Field = "Rows", New = result.Rows.ToString() } };
        changes.AddRange(result.Warnings.Select(w => new AuditChange { Field = "Warning", New = w }));
        await audit.LogAsync("Import", "Daily entry", null, $"Imported '{file.FileName}': {result.Days} day(s), {result.Rows} rows", changes);
        ViewBag.Result = result;
        return View();
    }
}

public class ReportController(AppDb db, AccessService access, AuditService audit) : Controller
{
    async Task<ReportViewModel> BuildAsync(DateTime? date)
    {
        var d = (date ?? DateTime.Today).Date;
        var allowed = access.AllowedFactories;
        var vm = new ReportViewModel
        {
            Date = d,
            Factories = await db.Factories.Where(f => allowed == null || allowed.Contains(f.Id)).OrderBy(f => f.SortOrder).ToListAsync()
        };
        var rates = await BudgetRates.LoadAsync(db);
        var entries = await db.Entries.Where(e => e.Date == d).ToListAsync();
        var sections = await db.Sections.Include(s => s.Department).OrderBy(s => s.Department!.SortOrder).ThenBy(s => s.SortOrder).ToListAsync();
        foreach (var s in sections)
        {
            var row = new ReportRow { Department = s.Department!.Name, Section = s.Name, Separate = s.Department.SeparateTotal };
            foreach (var f in vm.Factories)
            {
                var e = entries.FirstOrDefault(x => x.FactoryId == f.Id && x.SectionId == s.Id);
                // Always the Budget Master figure that applied on that date.
                var b = rates.Get(f.Id, s.Id, d);
                row.Cells[f.Id] = new ReportCell { Budget = b, Allocated = e?.Allocated ?? 0, Absent = e?.Absent ?? 0 };
            }
            if (row.Cells.Values.Any(c => c.Budget > 0 || c.Allocated > 0)) vm.Rows.Add(row);
        }
        return vm;
    }

    public async Task<IActionResult> Index(DateTime? date) => View(await BuildAsync(date));

    /// <summary>Excel download of the same report.</summary>
    public async Task<IActionResult> Export(DateTime? date)
    {
        var vm = await BuildAsync(date);
        var bytes = ExcelExport.Report(vm);
        await audit.LogAsync("Export", "Report", vm.Date.ToString("yyyy-MM-dd"), $"Report downloaded as Excel for {vm.Date:dd MMM yyyy} ({string.Join(", ", vm.Factories.Select(f => f.Name))})");
        return File(bytes, ExcelExport.ContentType, $"Cadre_Report_{vm.Date:yyyy-MM-dd}.xlsx");
    }
}