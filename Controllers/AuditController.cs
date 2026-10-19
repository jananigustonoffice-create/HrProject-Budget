using HrProject_Budget.Data;
using HrProject_Budget.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrProject_Budget.Controllers;

/// <summary>Activity log: who did what and when. Admin only (enforced by ScreenAuthorizationFilter). Read-only by design.</summary>
public class AuditController(AppDb db, AuditService audit) : Controller
{
    const int ExportMax = 20000;

    IQueryable<AuditLog> Apply(AuditFilter f)
    {
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        // the date pickers are local dates; times are stored in UTC
        if (f.From != null) { var from = DateTime.SpecifyKind(f.From.Value.Date, DateTimeKind.Local).ToUniversalTime(); q = q.Where(a => a.Time >= from); }
        if (f.To != null) { var to = DateTime.SpecifyKind(f.To.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime(); q = q.Where(a => a.Time < to); }
        if (!string.IsNullOrWhiteSpace(f.User)) q = q.Where(a => a.Username == f.User);
        if (!string.IsNullOrWhiteSpace(f.Type)) q = q.Where(a => a.Action == f.Type);
        if (!string.IsNullOrWhiteSpace(f.Entity)) q = q.Where(a => a.Entity == f.Entity);
        if (!string.IsNullOrWhiteSpace(f.EntityId)) q = q.Where(a => a.EntityId == f.EntityId);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var s = f.Q.Trim();
            q = q.Where(a => a.Summary.Contains(s) || a.Username.Contains(s) || (a.Details != null && a.Details.Contains(s)));
        }
        return q;
    }

    public async Task<IActionResult> Index(AuditFilter f)
    {
        if (f.Page < 1) f.Page = 1;
        if (Array.IndexOf(new[] { 25, 50, 100, 200 }, f.PageSize) < 0) f.PageSize = 50;
        var q = Apply(f);
        var vm = new AuditViewModel { Filter = f, Total = await q.CountAsync() };
        if (f.Page > vm.Pages) f.Page = vm.Pages;
        vm.Rows = await q.OrderByDescending(a => a.Time).ThenByDescending(a => a.Id).Skip((f.Page - 1) * vm.PageSize).Take(vm.PageSize).ToListAsync();

        // headline counts for the current filter
        var counts = await q.GroupBy(a => a.Action).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        int N(params string[] keys) => counts.Where(c => keys.Contains(c.Key)).Sum(c => c.N);
        vm.Logins = N("Login"); vm.FailedLogins = N("LoginFailed"); vm.DataChanges = N("Create", "Update", "Delete", "Import"); vm.Exports = N("Export");

        vm.Users = await db.AuditLogs.Select(a => a.Username).Distinct().OrderBy(x => x).ToListAsync();
        vm.Actions = await db.AuditLogs.Select(a => a.Action).Distinct().OrderBy(x => x).ToListAsync();
        vm.Entities = await db.AuditLogs.Select(a => a.Entity).Distinct().OrderBy(x => x).ToListAsync();
        return View(vm);
    }

    /// <summary>Excel download of the filtered log.</summary>
    public async Task<IActionResult> Export(AuditFilter f)
    {
        var rows = await Apply(f).OrderByDescending(a => a.Time).ThenByDescending(a => a.Id).Take(ExportMax).ToListAsync();
        var parts = new List<string>();
        if (f.From != null || f.To != null) parts.Add($"{f.From:dd MMM yyyy} – {f.To:dd MMM yyyy}");
        if (!string.IsNullOrWhiteSpace(f.User)) parts.Add("user " + f.User);
        if (!string.IsNullOrWhiteSpace(f.Type)) parts.Add("action " + f.Type);
        if (!string.IsNullOrWhiteSpace(f.Entity)) parts.Add("entity " + f.Entity + (string.IsNullOrWhiteSpace(f.EntityId) ? "" : " #" + f.EntityId));
        if (!string.IsNullOrWhiteSpace(f.Q)) parts.Add("search \"" + f.Q + "\"");
        var text = $"{rows.Count:N0} entries" + (parts.Count > 0 ? " · " + string.Join(" · ", parts) : "") + (rows.Count == ExportMax ? $" (first {ExportMax:N0} shown)" : "");
        var bytes = ExcelExport.Audit(rows, text);
        await audit.LogAsync("Export", "Activity log", null, $"Activity log downloaded as Excel ({rows.Count:N0} entries)");
        return File(bytes, ExcelExport.ContentType, $"Activity_Log_{DateTime.Now:yyyy-MM-dd_HHmm}.xlsx");
    }
}
