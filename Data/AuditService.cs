using System.Security.Claims;
using System.Text.Json;
using HrProject_Budget.Models;

namespace HrProject_Budget.Data;

/// <summary>
/// Writes history entries for things that are not plain data changes (sign-ins, downloads, imports, deletes).
/// Plain create / update / delete of the main data is captured automatically by <see cref="AppDb"/>.
/// </summary>
public class AuditService(AppDb db, IHttpContextAccessor http)
{
    public void Add(string action, string entity, string? entityId, string summary, IEnumerable<AuditChange>? changes = null, string? username = null, int? userId = null)
    {
        var ctx = http.HttpContext;
        var list = changes?.ToList();
        db.AuditLogs.Add(new AuditLog
        {
            Time = DateTime.UtcNow,
            UserId = userId ?? (int.TryParse(ctx?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null),
            Username = username ?? ctx?.User.FindFirstValue(ClaimTypes.Name) ?? "system",
            Action = action, Entity = entity, EntityId = entityId,
            Summary = summary.Length > 400 ? summary[..397] + "..." : summary,
            Details = list == null || list.Count == 0 ? null : JsonSerializer.Serialize(list),
            Ip = ctx?.Connection.RemoteIpAddress?.ToString()
        });
    }

    public async Task LogAsync(string action, string entity, string? entityId, string summary, IEnumerable<AuditChange>? changes = null, string? username = null, int? userId = null)
    {
        Add(action, entity, entityId, summary, changes, username, userId);
        await db.SaveChangesAsync();
    }
}
