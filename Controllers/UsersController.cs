using System.Security.Claims;
using HrProject_Budget.Data;
using HrProject_Budget.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrProject_Budget.Controllers;

/// <summary>Admin only (enforced by ScreenAuthorizationFilter).</summary>
public class UsersController(AppDb db, AuditService audit) : Controller
{
    readonly PasswordHasher<AppUser> _hasher = new();
    int CurrentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> Index()
    {
        ViewBag.FactoryNames = await db.Factories.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name);
        return View(await db.Users.Include(u => u.Permissions).Include(u => u.Factories).OrderBy(u => u.Username).ToListAsync());
    }

    static List<PermRow> BlankPerms() => Screens.All.Select(s => new PermRow { Screen = s.Key, Label = s.Label }).ToList();

    async Task FillLists() => ViewBag.Factories = await db.Factories.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync();

    public async Task<IActionResult> Create()
    {
        await FillLists();
        return View("Edit", new UserEditModel { Perms = BlankPerms() });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var u = await db.Users.Include(x => x.Permissions).Include(x => x.Factories).FirstOrDefaultAsync(x => x.Id == id);
        if (u == null) return NotFound();
        var perms = BlankPerms();
        foreach (var p in perms)
        {
            var hit = u.Permissions.FirstOrDefault(x => x.Screen == p.Screen);
            p.View = hit != null;
            p.Edit = hit?.CanEdit ?? false;
        }
        await FillLists();
        return View(new UserEditModel
        {
            Id = u.Id, Username = u.Username, FullName = u.FullName, Role = u.Role, IsActive = u.IsActive, Perms = perms,
            AllFactories = u.AllFactories, FactoryIds = u.Factories.Select(x => x.FactoryId).ToList()
        });
    }

    static string PermText(IEnumerable<(string Screen, bool Edit)> perms) =>
        perms.Any() ? string.Join(", ", perms.OrderBy(p => p.Screen).Select(p => p.Screen + (p.Edit ? " (edit)" : " (view)"))) : "none";

    [HttpPost]
    public async Task<IActionResult> Save(UserEditModel m)
    {
        m.Username = m.Username?.Trim() ?? "";
        // Labels are not posted; restore them.
        foreach (var p in m.Perms) p.Label = Screens.All.FirstOrDefault(s => s.Key == p.Screen)?.Label ?? p.Screen;
        var factoryNames = await db.Factories.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name);
        if (m.Role == Role.Admin) m.AllFactories = true;

        if (m.Id == 0 && string.IsNullOrWhiteSpace(m.Password)) ModelState.AddModelError(nameof(m.Password), "Password is required for a new user.");
        if (!string.IsNullOrWhiteSpace(m.Password) && PasswordPolicy.Check(m.Password) is { } bad) ModelState.AddModelError(nameof(m.Password), bad);
        if (await db.Users.AnyAsync(u => u.Id != m.Id && u.Username == m.Username)) ModelState.AddModelError(nameof(m.Username), "Username is already taken.");
        if (!m.AllFactories && !m.FactoryIds.Any(factoryNames.ContainsKey)) ModelState.AddModelError("", "Choose at least one factory, or tick All factories.");
        if (m.Id == CurrentId && (m.Role != Role.Admin || !m.IsActive)) ModelState.AddModelError("", "You cannot demote or deactivate your own account.");
        if (m.Id != 0 && m.Id != CurrentId && m.Role != Role.Admin && !await db.Users.AnyAsync(u => u.Id != m.Id && u.Role == Role.Admin && u.IsActive))
            ModelState.AddModelError("", "At least one active admin is required.");
        if (!ModelState.IsValid) { await FillLists(); return View("Edit", m); }

        AppUser u;
        string oldPerms = "none", oldFactories = "all factories";
        if (m.Id == 0) { u = new AppUser(); db.Users.Add(u); }
        else
        {
            u = await db.Users.Include(x => x.Permissions).Include(x => x.Factories).FirstAsync(x => x.Id == m.Id);
            oldPerms = u.Role == Role.User ? PermText(u.Permissions.Select(p => (p.Screen, p.CanEdit))) : u.Role == Role.Admin ? "all (admin)" : "all, view only";
            oldFactories = u.AllFactories ? "all factories" : string.Join(", ", u.Factories.Select(x => factoryNames.GetValueOrDefault(x.FactoryId, "#" + x.FactoryId)).OrderBy(x => x));
        }

        u.Username = m.Username;
        u.FullName = m.FullName;
        u.Role = m.Role;
        u.IsActive = m.IsActive;
        u.AllFactories = m.AllFactories;
        if (!string.IsNullOrWhiteSpace(m.Password))
        {
            u.PasswordHash = _hasher.HashPassword(u, m.Password);
            // A password set by an admin is temporary: the person must choose their own at next sign-in.
            if (m.Id != CurrentId) { u.MustChangePassword = true; u.FailedLogins = 0; u.LockoutEnd = null; }
        }
        else if (m.Id != 0 && m.IsActive) { u.FailedLogins = 0; u.LockoutEnd = null; }   // saving an account also clears a lockout

        // Screen permissions apply to the "User" role only; other roles are defined by the role itself.
        u.Permissions.Clear();
        if (m.Role == Role.User)
            foreach (var p in m.Perms.Where(p => (p.View || p.Edit) && Screens.All.Any(s => s.Key == p.Screen)))
                u.Permissions.Add(new UserPermission { Screen = p.Screen, CanEdit = p.Edit });

        // Factory access: none listed = all factories.
        u.Factories.Clear();
        if (!m.AllFactories)
            foreach (var fid in m.FactoryIds.Distinct().Where(factoryNames.ContainsKey)) u.Factories.Add(new UserFactory { FactoryId = fid });

        await db.SaveChangesAsync();   // the user row itself is recorded automatically

        // Screen / factory access lives in child rows, so its history entry is written here.
        var newPerms = u.Role == Role.User ? PermText(u.Permissions.Select(p => (p.Screen, p.CanEdit))) : u.Role == Role.Admin ? "all (admin)" : "all, view only";
        var newFactories = u.AllFactories ? "all factories" : string.Join(", ", u.Factories.Select(x => factoryNames.GetValueOrDefault(x.FactoryId, "#" + x.FactoryId)).OrderBy(x => x));
        var changes = new List<AuditChange>();
        if (m.Id == 0 || oldPerms != newPerms) changes.Add(new AuditChange { Field = "Screens", Old = m.Id == 0 ? null : oldPerms, New = newPerms });
        if (m.Id == 0 || oldFactories != newFactories) changes.Add(new AuditChange { Field = "Factories", Old = m.Id == 0 ? null : oldFactories, New = newFactories });
        if (changes.Count > 0)
            await audit.LogAsync("Update", "User access", u.Id.ToString(), $"Access {(m.Id == 0 ? "set" : "changed")} for '{u.Username}'", changes);

        TempData["Ok"] = $"User '{u.Username}' saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        if (id == CurrentId) TempData["Error"] = "You cannot delete your own account.";
        else
        {
            var u = await db.Users.FindAsync(id);
            if (u != null) { db.Users.Remove(u); await db.SaveChangesAsync(); }   // recorded automatically
        }
        return RedirectToAction(nameof(Index));
    }
}
