using HrProject_Budget.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Server-only settings file (not part of the project or publish output): put the production connection string here.
// Loaded last, so it wins over appsettings.json whatever ASPNETCORE_ENVIRONMENT IIS runs with.
builder.Configuration.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "appsettings.Local.json"), optional: true, reloadOnChange: true);
builder.Services.AddControllersWithViews(o => { o.Filters.Add<ScreenAuthorizationFilter>(); o.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()); });
builder.Services.AddHttpContextAccessor();
// SQL Server when DefaultConnection is a SQL Server string; otherwise a local SQLite file.
// Credentials are not stored in appsettings.json: use user-secrets (dev), or in production either
// appsettings.Local.json next to the published files or the ConnectionStrings__DefaultConnection environment variable.
var conn = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(conn))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured. Create appsettings.Local.json in " + builder.Environment.ContentRootPath + " (see appsettings.Local.json.example) or set the ConnectionStrings__DefaultConnection environment variable.");
    conn = "Data Source=budget.db";
}
builder.Services.AddDbContext<AppDb>(o =>
{
    if (conn.Contains("Server=", StringComparison.OrdinalIgnoreCase)) o.UseSqlServer(conn);
    else o.UseSqlite(conn);
});
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddAuthentication("Cookies").AddCookie("Cookies", o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/AccessDenied";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // Secure whenever the site is served over HTTPS    o.Cookie.Name = "hrbudget.auth";
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    AppDb.Seed(scope.ServiceProvider.GetRequiredService<AppDb>(), Path.Combine(app.Environment.ContentRootPath, "Data", "seed.json"), builder.Configuration["Seed:AdminPassword"]);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    await next();
});
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.Run();

