using System.ComponentModel.DataAnnotations;

namespace HrProject_Budget.Models;

/// <summary>One thing that happened in the system: who, when, what, and (for data changes) old -> new values.</summary>
public class AuditLog
{
    public int Id { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    [StringLength(100)] public string Username { get; set; } = "system";
    [StringLength(40)] public string Action { get; set; } = "";     // Create, Update, Delete, Login, Logout, LoginFailed, Import, Export, ...
    [StringLength(60)] public string Entity { get; set; } = "";     // Factory, Budget, Daily entry, User, Auth, Report, ...
    [StringLength(60)] public string? EntityId { get; set; }
    [StringLength(400)] public string Summary { get; set; } = "";
    public string? Details { get; set; }                            // JSON list of AuditChange
    [StringLength(60)] public string? Ip { get; set; }
}

public class AuditChange
{
    public string Field { get; set; } = "";
    public string? Old { get; set; }
    public string? New { get; set; }
}

/// <summary>Limits a user to specific factories (a user with AllFactories = true has no rows here).</summary>
public class UserFactory
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int FactoryId { get; set; }
    public Factory? Factory { get; set; }
}

public class AuditFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? User { get; set; }
    public string? Type { get; set; }   // not "Action": that name collides with the route value "action"
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class AuditViewModel
{
    public AuditFilter Filter { get; set; } = new();
    public List<AuditLog> Rows { get; set; } = new();
    public int Total { get; set; }
    public int PageSize => Filter.PageSize;
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
    public List<string> Users { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    public List<string> Entities { get; set; } = new();
    public int Logins { get; set; }
    public int FailedLogins { get; set; }
    public int DataChanges { get; set; }
    public int Exports { get; set; }
}
