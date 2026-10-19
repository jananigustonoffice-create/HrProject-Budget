using HrProject_Budget.Data;
using HrProject_Budget.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace HrProject_Budget.Controllers;

// Create / update of master data is captured automatically by AppDb. Deletes below use ExecuteDelete
// (which skips change tracking), so each one writes its own history entry.

public class FactoriesController(AppDb db, AccessService access, AuditService audit) : Controller
{
    IActionResult Denied() => RedirectToAction("AccessDenied", "Account");

    public async Task<IActionResult> Index()
    {
        var allowed = access.AllowedFactories;
        return View(await db.Factories.Where(x => allowed == null || allowed.Contains(x.Id)).OrderBy(x => x.SortOrder).ToListAsync());
    }

    public IActionResult Create() => access.SeesAllFactories ? View("Edit", new Factory()) : Denied();

    public async Task<IActionResult> Edit(int id) => access.CanSeeFactory(id) ? View(await db.Factories.FindAsync(id)) : Denied();

    [HttpPost]
    public async Task<IActionResult> Save(Factory m)
    {
        if (m.Id == 0 ? !access.SeesAllFactories : !access.CanSeeFactory(m.Id)) return Denied();
        if (!ModelState.IsValid) return View("Edit", m);
        if (m.Id == 0) db.Add(m); else db.Update(m);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        if (!access.CanSeeFactory(id)) return Denied();
        var f = await db.Factories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (f == null) return RedirectToAction(nameof(Index));
        if (await db.Entries.AnyAsync(e => e.FactoryId == id)) TempData["Error"] = "Factory has daily entries and cannot be deleted.";
        else
        {
            var budgets = await db.Budgets.Where(b => b.FactoryId == id).ExecuteDeleteAsync();
            await db.BudgetHistories.Where(b => b.FactoryId == id).ExecuteDeleteAsync();
            await db.Factories.Where(x => x.Id == id).ExecuteDeleteAsync();
            await audit.LogAsync("Delete", "Factory", id.ToString(), $"Factory '{f.Name}' deleted" + (budgets > 0 ? $" (with {budgets} budget row{(budgets == 1 ? "" : "s")})" : ""),
                new[] { new AuditChange { Field = "Name", Old = f.Name }, new AuditChange { Field = "FullName", Old = f.FullName } });
        }
        return RedirectToAction(nameof(Index));
    }
}

public class DepartmentsController(AppDb db, AuditService audit) : Controller
{
    public async Task<IActionResult> Index() => View(await db.Departments.OrderBy(x => x.SortOrder).ToListAsync());
    public IActionResult Create() => View("Edit", new Department());
    public async Task<IActionResult> Edit(int id) => View(await db.Departments.FindAsync(id));

    [HttpPost]
    public async Task<IActionResult> Save(Department m)
    {
        if (!ModelState.IsValid) return View("Edit", m);
        if (m.Id == 0) db.Add(m); else db.Update(m);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var d = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return RedirectToAction(nameof(Index));
        if (await db.Sections.AnyAsync(s => s.DepartmentId == id)) TempData["Error"] = "Department has sections and cannot be deleted.";
        else
        {
            await db.Departments.Where(x => x.Id == id).ExecuteDeleteAsync();
            await audit.LogAsync("Delete", "Department", id.ToString(), $"Department '{d.Name}' deleted", new[] { new AuditChange { Field = "Name", Old = d.Name } });
        }
        return RedirectToAction(nameof(Index));
    }
}

public class SectionsController(AppDb db, AuditService audit) : Controller
{
    void Lists(int? dep = null) => ViewBag.Departments = new SelectList(db.Departments.OrderBy(d => d.SortOrder), "Id", "Name", dep);

    public async Task<IActionResult> Index() =>
        View(await db.Sections.Include(s => s.Department).OrderBy(s => s.Department!.SortOrder).ThenBy(s => s.SortOrder).ToListAsync());

    public IActionResult Create() { Lists(); return View("Edit", new Section()); }

    public async Task<IActionResult> Edit(int id)
    {
        var m = await db.Sections.FindAsync(id);
        Lists(m?.DepartmentId);
        return View(m);
    }

    [HttpPost]
    public async Task<IActionResult> Save(Section m)
    {
        ModelState.Remove(nameof(Section.Department));
        if (ModelState.IsValid && await db.Sections.AnyAsync(s => s.Id != m.Id && s.DepartmentId == m.DepartmentId && s.Name == m.Name))
            ModelState.AddModelError("Name", "This section already exists in the department.");
        if (!ModelState.IsValid) { Lists(m.DepartmentId); return View("Edit", m); }
        if (m.Id == 0) db.Add(m); else db.Update(m);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await db.Sections.AsNoTracking().Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return RedirectToAction(nameof(Index));
        if (await db.Entries.AnyAsync(e => e.SectionId == id)) TempData["Error"] = "Section has daily entries and cannot be deleted.";
        else
        {
            var budgets = await db.Budgets.Where(b => b.SectionId == id).ExecuteDeleteAsync();
            await db.BudgetHistories.Where(b => b.SectionId == id).ExecuteDeleteAsync();
            await db.Sections.Where(x => x.Id == id).ExecuteDeleteAsync();
            await audit.LogAsync("Delete", "Section", id.ToString(), $"Section '{s.Department?.Name} / {s.Name}' deleted" + (budgets > 0 ? $" (with {budgets} budget row{(budgets == 1 ? "" : "s")})" : ""),
                new[] { new AuditChange { Field = "Name", Old = s.Name }, new AuditChange { Field = "Department", Old = s.Department?.Name } });
        }
        return RedirectToAction(nameof(Index));
    }
}

public class BudgetsController(AppDb db, AccessService access, AuditService audit) : Controller
{
    IActionResult Denied() => RedirectToAction("AccessDenied", "Account");

    void Lists(int? f = null, int? s = null)
    {
        var allowed = access.AllowedFactories;
        ViewBag.Factories = new SelectList(db.Factories.Where(x => allowed == null || allowed.Contains(x.Id)).OrderBy(x => x.SortOrder), "Id", "Name", f);
        var secs = db.Sections.Include(x => x.Department).OrderBy(x => x.Department!.SortOrder).ThenBy(x => x.SortOrder)
            .Select(x => new { x.Id, Label = x.Department!.Name + " - " + x.Name }).ToList();
        ViewBag.Sections = new SelectList(secs, "Id", "Label", s);
    }

    public async Task<IActionResult> Index(int? factoryId)
    {
        Lists(factoryId);
        ViewBag.FactoryId = factoryId;
        var allowed = access.AllowedFactories;
        var q = db.Budgets.Include(b => b.Factory).Include(b => b.Section).ThenInclude(s => s!.Department).Where(b => allowed == null || allowed.Contains(b.FactoryId));
        if (factoryId > 0) q = q.Where(b => b.FactoryId == factoryId);
        var list = (await q.ToListAsync())
            .OrderBy(b => b.Factory!.SortOrder).ThenBy(b => b.Section!.Department!.SortOrder).ThenBy(b => b.Section!.SortOrder).ToList();
        return View(list);
    }

    public IActionResult Create() { Lists(); return View("Edit", new BudgetMaster()); }

    public async Task<IActionResult> Edit(int id)
    {
        var m = await db.Budgets.FindAsync(id);
        if (m != null && !access.CanSeeFactory(m.FactoryId)) return Denied();
        Lists(m?.FactoryId, m?.SectionId);
        if (m != null) ViewBag.History = await db.BudgetHistories.AsNoTracking().Where(h => h.FactoryId == m.FactoryId && h.SectionId == m.SectionId).OrderByDescending(h => h.EffectiveFrom).ToListAsync();
        return View(m);
    }

    [HttpPost]
    public async Task<IActionResult> Save(BudgetMaster m, DateTime? effectiveFrom)
    {
        if (!access.CanSeeFactory(m.FactoryId)) return Denied();
        ModelState.Remove(nameof(BudgetMaster.Factory));
        ModelState.Remove(nameof(BudgetMaster.Section));
        if (ModelState.IsValid && await db.Budgets.AnyAsync(b => b.Id != m.Id && b.FactoryId == m.FactoryId && b.SectionId == m.SectionId))
            ModelState.AddModelError("", "A budget already exists for this factory and section. Edit it instead.");
        if (!ModelState.IsValid) { Lists(m.FactoryId, m.SectionId); return View("Edit", m); }
        // The new budget applies from this date onwards; earlier days keep the budget they had.
        var eff = (effectiveFrom ?? (m.Id == 0 ? BudgetRates.Beginning : DateTime.Today)).Date;
        if (eff < BudgetRates.Beginning) eff = BudgetRates.Beginning;
        var rates = await BudgetRates.LoadAsync(db, m.FactoryId);
        var before = rates.Get(m.FactoryId, m.SectionId, eff);
        var hist = await db.BudgetHistories.Where(h => h.FactoryId == m.FactoryId && h.SectionId == m.SectionId).ToListAsync();
        var row = hist.FirstOrDefault(h => h.EffectiveFrom.Date == eff);
        if (row == null) { row = new BudgetHistory { FactoryId = m.FactoryId, SectionId = m.SectionId, EffectiveFrom = eff }; db.BudgetHistories.Add(row); hist.Add(row); }
        row.Budget = m.Budget;
        m.Budget = hist.OrderBy(h => h.EffectiveFrom).Last().Budget;   // Budget Master always shows the latest budget
        if (m.Id == 0) db.Add(m); else db.Update(m);
        await db.SaveChangesAsync();

        // Every day already saved from that date (until the next change) takes the new budget, whatever snapshot it carried
        // (a saved day's own budget wins in the report, so a stale snapshot would otherwise keep showing the old figure).
        var next = hist.Where(h => h.EffectiveFrom.Date > eff).Select(h => (DateTime?)h.EffectiveFrom.Date).Min();
        var nv = row.Budget;
        await db.Entries.Where(e => e.FactoryId == m.FactoryId && e.SectionId == m.SectionId && e.Date >= eff && e.Budget != nv && (next == null || e.Date < next))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Budget, nv));
        if (before != nv)
        {
            await audit.LogAsync("Update", "Budget", m.Id.ToString(), $"Budget {before} -> {nv} effective from {eff:dd MMM yyyy}",
                new[] { new AuditChange { Field = "Budget", Old = before.ToString(), New = nv.ToString() }, new AuditChange { Field = "Effective from", New = eff.ToString("yyyy-MM-dd") } });
        }
        return RedirectToAction(nameof(Index), new { factoryId = m.FactoryId });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var b = await db.Budgets.AsNoTracking().Include(x => x.Factory).Include(x => x.Section).ThenInclude(s => s!.Department).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return RedirectToAction(nameof(Index));
        if (!access.CanSeeFactory(b.FactoryId)) return Denied();
        await db.Budgets.Where(x => x.Id == id).ExecuteDeleteAsync();
        // Past days keep their budget; from today there is none.
        var today = DateTime.Today;
        var hrow = await db.BudgetHistories.FirstOrDefaultAsync(h => h.FactoryId == b.FactoryId && h.SectionId == b.SectionId && h.EffectiveFrom == today);
        if (hrow == null) db.BudgetHistories.Add(new BudgetHistory { FactoryId = b.FactoryId, SectionId = b.SectionId, EffectiveFrom = today, Budget = 0 }); else hrow.Budget = 0;
        await db.SaveChangesAsync();
        await audit.LogAsync("Delete", "Budget", id.ToString(), $"Budget {b.Factory?.Name} / {b.Section?.Department?.Name} / {b.Section?.Name} deleted (was {b.Budget})",
            new[] { new AuditChange { Field = "Budget", Old = b.Budget.ToString() } });
        return RedirectToAction(nameof(Index));
    }
}
