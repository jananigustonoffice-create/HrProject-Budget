using System.Security.Claims;
using HrProject_Budget.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace HrProject_Budget.Data;

public record ScreenInfo(string Key, string Label, string Icon, string Controller);

public static class Screens
{
    public static readonly ScreenInfo[] All =
    {
        new("Entry", "Daily Entry", "pencil-square", "Entry"),
        new("Report", "Report", "bar-chart-line", "Report"),
        new("Factories", "Factory Master", "buildings", "Factories"),
        new("Departments", "Department Master", "diagram-3", "Departments"),
        new("Sections", "Section Master", "grid-3x3-gap", "Sections"),
        new("Budgets", "Budget Master", "cash-coin", "Budgets"),
    };

    public static ScreenInfo? ForController(string c) => All.FirstOrDefault(s => s.Controller == c);
}

/// <summary>
/// Per-request view of what the signed-in user may do. Read from the database each request,
/// so permission changes and deactivation take effect immediately.
/// </summary>
public class AccessService(AppDb db, IHttpContextAccessor http)
{
    bool _loaded;
    AppUser? _user;
    Dictionary<string, bool> _perms = new();
    HashSet<int>? _factories;                    // null = every factory

    public AppUser? User { get { Load(); return _user; } }
    public bool IsAdmin => User?.Role == Role.Admin;
    public bool IsSuperUser => User?.Role == Role.SuperUser;

    void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var idStr = http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idStr, out var id)) return;
        _user = db.Users.AsNoTracking().Include(u => u.Permissions).Include(u => u.Factories).FirstOrDefault(u => u.Id == id && u.IsActive);
        if (_user != null)
        {
            _perms = _user.Permissions.ToDictionary(p => p.Screen, p => p.CanEdit);
            _factories = _user.Role == Role.Admin || _user.AllFactories ? null : _user.Factories.Select(x => x.FactoryId).ToHashSet();
        }
    }

    /// <summary>The factories this user may work with; null means all of them.</summary>
    public HashSet<int>? AllowedFactories { get { Load(); return _factories; } }
    public bool CanSeeFactory(int factoryId) => AllowedFactories == null || AllowedFactories.Contains(factoryId);
    public bool SeesAllFactories => AllowedFactories == null;

    public bool CanView(string screen)
    {
        var u = User;
        if (u == null) return false;
        return u.Role != Role.User || _perms.ContainsKey(screen);
    }

    public bool CanEdit(string screen)
    {
        var u = User;
        if (u == null) return false;
        if (u.Role == Role.Admin) return true;
        return u.Role == Role.User && _perms.TryGetValue(screen, out var e) && e;
    }
}

/// <summary>Global filter: GET needs view rights on the controller's screen, any other verb needs edit rights.</summary>
public class ScreenAuthorizationFilter(AccessService access) : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var controller = (string?)ctx.RouteData.Values["controller"] ?? "";
        if (controller is "Account" or "Error") return Task.CompletedTask;
        if (ctx.HttpContext.User.Identity?.IsAuthenticated != true) return Task.CompletedTask; // cookie auth challenges

        if (access.User == null)
        {
            ctx.Result = new RedirectToActionResult("Expired", "Account", null); // deactivated or deleted
            return Task.CompletedTask;
        }
        if (access.User.MustChangePassword)
        {
            ctx.Result = new RedirectToActionResult("ChangePassword", "Account", null);
            return Task.CompletedTask;
        }
        if (controller == "Home") return Task.CompletedTask;
        if (controller is "Users" or "Audit")
        {
            if (!access.IsAdmin) ctx.Result = new RedirectToActionResult("AccessDenied", "Account", null);
            return Task.CompletedTask;
        }
        var screen = Screens.ForController(controller);
        if (screen == null) return Task.CompletedTask;

        // Deleting master data is an admin-only action, separate from the edit right.
        var action = (string?)ctx.RouteData.Values["action"] ?? "";
        if (action == "Delete" && !access.IsAdmin)
        {
            ctx.Result = new RedirectToActionResult("AccessDenied", "Account", null);
            return Task.CompletedTask;
        }

        var write = !HttpMethods.IsGet(ctx.HttpContext.Request.Method) && !HttpMethods.IsHead(ctx.HttpContext.Request.Method);
        var ok = write ? access.CanEdit(screen.Key) : access.CanView(screen.Key);
        if (!ok) ctx.Result = new RedirectToActionResult("AccessDenied", "Account", null);
        return Task.CompletedTask;
    }
}
