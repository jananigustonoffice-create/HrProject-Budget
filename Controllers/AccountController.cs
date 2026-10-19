using System.Security.Claims;
using HrProject_Budget.Data;
using HrProject_Budget.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrProject_Budget.Controllers;

public class AccountController(AppDb db, AuditService audit) : Controller
{
    readonly PasswordHasher<AppUser> _hasher = new();
    const int MaxFailures = 5, LockMinutes = 10;
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl) =>
        User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Home") : View(new LoginModel { ReturnUrl = returnUrl });

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Login(LoginModel m)
    {
        if (!ModelState.IsValid) return View(m);
        var name = m.Username.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == name);
        if (user is { IsActive: true } && user.LockoutEnd > DateTime.UtcNow)
        {
            var mins = (int)Math.Ceiling((user.LockoutEnd.Value - DateTime.UtcNow).TotalMinutes);
            await audit.LogAsync("LoginFailed", "Auth", user.Id.ToString(), $"Sign-in blocked for '{name}' (account locked)", null, name, user.Id);
            ModelState.AddModelError("", $"Too many failed attempts. Try again in {mins} minute(s).");
            m.Password = "";
            return View(m);
        }
        var ok = user is { IsActive: true } && _hasher.VerifyHashedPassword(user, user.PasswordHash, m.Password) != PasswordVerificationResult.Failed;
        if (!ok)
        {
            var why = user == null ? "unknown user" : !user.IsActive ? "account disabled" : "wrong password";
            if (user is { IsActive: true })
            {
                // ExecuteUpdate keeps the counter out of the change history.
                var fails = user.FailedLogins + 1;
                var lockUntil = fails >= MaxFailures ? DateTime.UtcNow.AddMinutes(LockMinutes) : (DateTime?)null;
                await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.FailedLogins, lockUntil != null ? 0 : fails)
                    .SetProperty(x => x.LockoutEnd, lockUntil));
                if (lockUntil != null) why += $"; locked for {LockMinutes} minutes";
            }
            await audit.LogAsync("LoginFailed", "Auth", user?.Id.ToString(), $"Failed sign-in for '{name}' ({why})", null, name, user?.Id);
            ModelState.AddModelError("", "Invalid username or password.");
            m.Password = "";
            return View(m);
        }
        if (user!.FailedLogins != 0 || user.LockoutEnd != null)
            await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedLogins, 0).SetProperty(x => x.LockoutEnd, (DateTime?)null));
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user!.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
        };
        await HttpContext.SignInAsync("Cookies", new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")),
            new AuthenticationProperties { IsPersistent = m.RememberMe, ExpiresUtc = m.RememberMe ? DateTimeOffset.UtcNow.AddDays(7) : null, AllowRefresh = true });
        await audit.LogAsync("Login", "Auth", user.Id.ToString(), $"'{user.Username}' signed in", null, user.Username, user.Id);
        return !string.IsNullOrEmpty(m.ReturnUrl) && Url.IsLocalUrl(m.ReturnUrl) ? LocalRedirect(m.ReturnUrl) : RedirectToAction("Index", "Home");
    }

    // POST only, so another website cannot sign users out with a link or image.
    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Logout()
    {
        if (User.Identity?.IsAuthenticated == true)
            await audit.LogAsync("Logout", "Auth", User.FindFirstValue(ClaimTypes.NameIdentifier), $"'{User.Identity.Name}' signed out");
        await HttpContext.SignOutAsync("Cookies");
        return RedirectToAction(nameof(Login));
    }

    /// <summary>Ends a session whose user was deactivated or deleted (redirected here by the access filter).</summary>
    [AllowAnonymous]
    public async Task<IActionResult> Expired()
    {
        await HttpContext.SignOutAsync("Cookies");
        return RedirectToAction(nameof(Login));
    }

    public IActionResult AccessDenied() => View();

    public IActionResult ChangePassword() => View(new ChangePasswordModel());

    [HttpPost]
    public async Task<IActionResult> ChangePassword(ChangePasswordModel m)
    {
        if (!ModelState.IsValid) return View(m);
        var id = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await db.Users.FindAsync(id);
        if (user == null || _hasher.VerifyHashedPassword(user, user.PasswordHash, m.Current) == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError(nameof(m.Current), "Current password is incorrect.");
            return View(m);
        }
        if (PasswordPolicy.Check(m.New) is { } bad) { ModelState.AddModelError(nameof(m.New), bad); return View(m); }
        if (m.New == m.Current) { ModelState.AddModelError(nameof(m.New), "Choose a password different from the current one."); return View(m); }
        user.PasswordHash = _hasher.HashPassword(user, m.New);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();          // recorded automatically as a password change (the password itself is never logged)
        TempData["Ok"] = "Password changed.";
        return RedirectToAction("Index", "Home");
    }
}
