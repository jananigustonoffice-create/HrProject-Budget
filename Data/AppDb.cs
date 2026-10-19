using System.Security.Claims;
using System.Text.Json;
using HrProject_Budget.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HrProject_Budget.Data;

public class AppDb : DbContext
{
    readonly IHttpContextAccessor? _http;
    public AppDb(DbContextOptions<AppDb> o, IHttpContextAccessor? http = null) : base(o) { _http = http; }

    public DbSet<Factory> Factories => Set<Factory>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<BudgetMaster> Budgets => Set<BudgetMaster>();
    public DbSet<DailyEntry> Entries => Set<DailyEntry>();
    public DbSet<BudgetHistory> BudgetHistories => Set<BudgetHistory>();

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<UserFactory> UserFactories => Set<UserFactory>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>Turns automatic change capture off (used while seeding the first-run data).</summary>
    public bool AuditOff { get; set; }
    /// <summary>Bulk imports write one summary entry instead of one per factory/day.</summary>
    public bool SuppressEntryAudit { get; set; }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.Username).IsUnique();
        b.Entity<AppUser>().Property(u => u.Role).HasConversion<string>();
        b.Entity<UserPermission>().HasIndex(p => new { p.UserId, p.Screen }).IsUnique();
        b.Entity<UserPermission>().HasOne(p => p.User).WithMany(u => u.Permissions).HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserFactory>().HasIndex(p => new { p.UserId, p.FactoryId }).IsUnique();
        b.Entity<UserFactory>().HasOne(p => p.User).WithMany(u => u.Factories).HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserFactory>().HasOne(p => p.Factory).WithMany().HasForeignKey(p => p.FactoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Section>().HasIndex(s => new { s.DepartmentId, s.Name }).IsUnique();
        b.Entity<BudgetMaster>().HasIndex(x => new { x.FactoryId, x.SectionId }).IsUnique();
        b.Entity<DailyEntry>().HasIndex(x => new { x.Date, x.FactoryId, x.SectionId }).IsUnique();
        b.Entity<BudgetHistory>().ToTable("BudgetHistory");
        b.Entity<BudgetHistory>().HasIndex(x => new { x.FactoryId, x.SectionId, x.EffectiveFrom }).IsUnique();
        b.Entity<AuditLog>().HasIndex(x => x.Time);
    }

    // ===================================================================================
    //  Automatic change history: every create / update / delete of the main data is
    //  recorded with who did it and the old -> new values.
    // ===================================================================================
    sealed record Pending(AuditLog Log, EntityEntry? Entry);

    (int? Id, string Name, string? Ip) Current()
    {
        var ctx = _http?.HttpContext;
        var id = ctx?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var name = ctx?.User.FindFirstValue(ClaimTypes.Name);
        return (int.TryParse(id, out var i) ? i : null, string.IsNullOrEmpty(name) ? "system" : name, ctx?.Connection.RemoteIpAddress?.ToString());
    }

    static string? Fmt(object? v) => v switch
    {
        null => null,
        bool b => b ? "Yes" : "No",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm"),
        _ => v.ToString()
    };

    List<Pending> Capture()
    {
        var result = new List<Pending>();
        if (AuditOff) return result;
        ChangeTracker.DetectChanges();
        var entries = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity is Factory or Department or Section or BudgetMaster or DailyEntry or AppUser)
            .ToList();
        if (entries.Count == 0) return result;

        var who = Current();
        // readable names for foreign keys
        Dictionary<int, string>? fac = null, dep = null, sec = null;
        string Fac(int id) { fac ??= Factories.AsNoTracking().ToDictionary(x => x.Id, x => x.Name); return fac.GetValueOrDefault(id, "#" + id); }
        string Dep(int id) { dep ??= Departments.AsNoTracking().ToDictionary(x => x.Id, x => x.Name); return dep.GetValueOrDefault(id, "#" + id); }
        string Sec(int id)
        {
            sec ??= Sections.AsNoTracking().Include(s => s.Department).ToDictionary(x => x.Id, x => (x.Department?.Name ?? "?") + " / " + x.Name);
            return sec.GetValueOrDefault(id, "#" + id);
        }

        AuditLog NewLog(string action, string entity, string? entityId, string summary, List<AuditChange> ch) => new()
        {
            Time = DateTime.UtcNow, UserId = who.Id, Username = who.Name, Ip = who.Ip,
            Action = action, Entity = entity, EntityId = entityId,
            Summary = summary.Length > 400 ? summary[..397] + "..." : summary,
            Details = ch.Count == 0 ? null : JsonSerializer.Serialize(ch)
        };

        List<AuditChange> Diff(EntityEntry e, params string[] skip)
        {
            var list = new List<AuditChange>();
            foreach (var p in e.Properties)
            {
                var n = p.Metadata.Name;
                if (n == "Id" || n == "CreatedAt" || n == "FailedLogins" || n == "LockoutEnd" || skip.Contains(n)) continue;
                if (n == "PasswordHash")
                {
                    if (e.State == EntityState.Added || (e.State == EntityState.Modified && p.IsModified)) list.Add(new AuditChange { Field = "Password", Old = null, New = e.State == EntityState.Added ? "set" : "changed" });
                    continue;
                }
                if (n.EndsWith("Id")) continue;   // foreign keys are shown by name instead
                switch (e.State)
                {
                    case EntityState.Added: if (p.CurrentValue != null) list.Add(new AuditChange { Field = n, New = Fmt(p.CurrentValue) }); break;
                    case EntityState.Deleted: list.Add(new AuditChange { Field = n, Old = Fmt(p.OriginalValue) }); break;
                    case EntityState.Modified:
                        if (p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)) list.Add(new AuditChange { Field = n, Old = Fmt(p.OriginalValue), New = Fmt(p.CurrentValue) });
                        break;
                }
            }
            return list;
        }

        string Suffix(EntityEntry e, List<AuditChange> ch) =>
            e.State == EntityState.Modified && ch.Count == 1 ? $": {ch[0].Field} {ch[0].Old ?? "-"} → {ch[0].New ?? "-"}"
            : e.State == EntityState.Modified ? $" ({ch.Count} fields changed)" : "";
        static string Verb(EntityState s) => s == EntityState.Added ? "Create" : s == EntityState.Deleted ? "Delete" : "Update";
        static string Past(EntityState s) => s == EntityState.Added ? "created" : s == EntityState.Deleted ? "deleted" : "updated";

        foreach (var e in entries.Where(e => e.Entity is not DailyEntry))
        {
            var ch = Diff(e);
            if (e.State == EntityState.Modified && ch.Count == 0) continue;
            string entity, label;
            switch (e.Entity)
            {
                case Factory f: entity = "Factory"; label = $"'{f.Name}'"; break;
                case Department d: entity = "Department"; label = $"'{d.Name}'"; break;
                case Section s:
                    entity = "Section"; label = $"'{Dep(s.DepartmentId)} / {s.Name}'";
                    var dp = e.Property("DepartmentId");
                    if (e.State == EntityState.Modified && dp.IsModified) ch.Add(new AuditChange { Field = "Department", Old = Dep((int)dp.OriginalValue!), New = Dep((int)dp.CurrentValue!) });
                    break;
                case BudgetMaster b: entity = "Budget"; label = $"{Fac(b.FactoryId)} / {Sec(b.SectionId)}"; break;
                case AppUser u: entity = "User"; label = $"'{u.Username}'"; break;
                default: continue;
            }
            result.Add(new Pending(NewLog(Verb(e.State), entity, e.State == EntityState.Added ? null : e.Property("Id").CurrentValue?.ToString(),
                $"{entity} {label} {Past(e.State)}{Suffix(e, ch)}", ch), e.State == EntityState.Added ? e : null));
        }

        // daily entries: one history line per date + factory, listing the sections that changed
        if (!SuppressEntryAudit)
            foreach (var g in entries.Where(e => e.Entity is DailyEntry).GroupBy(e => (((DailyEntry)e.Entity).Date, ((DailyEntry)e.Entity).FactoryId)))
            {
                var ch = new List<AuditChange>();
                foreach (var e in g)
                {
                    var de = (DailyEntry)e.Entity;
                    var section = Sec(de.SectionId);
                    foreach (var c in Diff(e, "Date")) ch.Add(new AuditChange { Field = $"{section} · {c.Field}", Old = c.Old, New = c.New });
                }
                if (ch.Count == 0) continue;
                var states = g.Select(x => x.State).Distinct().ToList();
                var state = states.Count == 1 ? states[0] : EntityState.Modified;
                var n = g.Count();
                result.Add(new Pending(NewLog(Verb(state), "Daily entry", $"{g.Key.Date:yyyy-MM-dd}|{g.Key.FactoryId}",
                    $"Daily entry {g.Key.Date:dd MMM yyyy} · {Fac(g.Key.FactoryId)}: {n} section{(n == 1 ? "" : "s")} {Past(state)}", ch), null));
            }
        return result;
    }

    static void ResolveIds(List<Pending> pending)
    {
        foreach (var p in pending.Where(p => p.Entry != null && p.Log.EntityId == null))
            p.Log.EntityId = p.Entry!.Property("Id").CurrentValue?.ToString();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var pending = Capture();
        var n = base.SaveChanges(acceptAllChangesOnSuccess);
        if (pending.Count > 0)
        {
            ResolveIds(pending);
            try { AuditLogs.AddRange(pending.Select(p => p.Log)); base.SaveChanges(acceptAllChangesOnSuccess); }
            catch (Exception ex) { Console.Error.WriteLine("Audit write failed: " + ex.Message); }
        }
        return n;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var pending = Capture();
        var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (pending.Count > 0)
        {
            ResolveIds(pending);
            try { AuditLogs.AddRange(pending.Select(p => p.Log)); await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
            catch (Exception ex) { Console.Error.WriteLine("Audit write failed: " + ex.Message); }
        }
        return n;
    }

    // ===================================================================================
    //  Schema upgrade + first-run seed
    // ===================================================================================
    static readonly string[] SeparateByDefault = { "Training Section", "Pregnant Mothers" };

    static bool Scalar(AppDb db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    static bool HasColumn(AppDb db, string table, string column) => Scalar(db, db.Database.IsSqlite()
        ? $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'"
        : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'");

    static bool HasTable(AppDb db, string table) => Scalar(db, db.Database.IsSqlite()
        ? $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'"
        : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{table}'");

    /// <summary>Adds tables/columns introduced after a database was first created (EnsureCreated does not alter tables).</summary>
    static void UpgradeSchema(AppDb db)
    {
        bool lite = db.Database.IsSqlite();
        if (!HasColumn(db, "Departments", "SeparateTotal"))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE Departments ADD SeparateTotal " + (lite ? "INTEGER" : "bit") + " NOT NULL DEFAULT 0");
            db.Database.ExecuteSqlRaw("UPDATE Departments SET SeparateTotal = 1 WHERE Name IN ('Training Section', 'Pregnant Mothers')");
        }
        if (!HasColumn(db, "Users", "AllFactories"))
            db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD AllFactories " + (lite ? "INTEGER" : "bit") + " NOT NULL DEFAULT 1");
        // Each column is checked on its own so an interrupted upgrade can be completed on the next start.
        if (!HasColumn(db, "Users", "MustChangePassword"))
            db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD MustChangePassword " + (lite ? "INTEGER" : "bit") + " NOT NULL DEFAULT 0");
        if (!HasColumn(db, "Users", "FailedLogins"))
            db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD FailedLogins " + (lite ? "INTEGER" : "int") + " NOT NULL DEFAULT 0");
        if (!HasColumn(db, "Users", "LockoutEnd"))
            db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD LockoutEnd " + (lite ? "TEXT" : "datetime2") + " NULL");
        if (!HasTable(db, "UserFactories"))
        {
            db.Database.ExecuteSqlRaw(lite
                ? "CREATE TABLE UserFactories (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE, FactoryId INTEGER NOT NULL REFERENCES Factories(Id) ON DELETE CASCADE)"
                : "CREATE TABLE UserFactories (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, UserId int NOT NULL REFERENCES Users(Id) ON DELETE CASCADE, FactoryId int NOT NULL REFERENCES Factories(Id) ON DELETE CASCADE)");
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IX_UserFactories_UserId_FactoryId ON UserFactories (UserId, FactoryId)");
        }
        if (!HasTable(db, "AuditLogs"))
        {
            db.Database.ExecuteSqlRaw(lite
                ? "CREATE TABLE AuditLogs (Id INTEGER PRIMARY KEY AUTOINCREMENT, Time TEXT NOT NULL, UserId INTEGER NULL, Username TEXT NOT NULL, Action TEXT NOT NULL, Entity TEXT NOT NULL, EntityId TEXT NULL, Summary TEXT NOT NULL, Details TEXT NULL, Ip TEXT NULL)"
                : "CREATE TABLE AuditLogs (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, [Time] datetime2 NOT NULL, UserId int NULL, Username nvarchar(100) NOT NULL, [Action] nvarchar(40) NOT NULL, Entity nvarchar(60) NOT NULL, EntityId nvarchar(60) NULL, Summary nvarchar(400) NOT NULL, Details nvarchar(max) NULL, Ip nvarchar(60) NULL)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IX_AuditLogs_Time ON AuditLogs (Time)");
        }
        if (!HasTable(db, "BudgetHistory"))
        {
            db.Database.ExecuteSqlRaw(lite
                ? "CREATE TABLE BudgetHistory (Id INTEGER PRIMARY KEY AUTOINCREMENT, FactoryId INTEGER NOT NULL, SectionId INTEGER NOT NULL, EffectiveFrom TEXT NOT NULL, Budget INTEGER NOT NULL)"
                : "CREATE TABLE BudgetHistory (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, FactoryId int NOT NULL, SectionId int NOT NULL, EffectiveFrom datetime2 NOT NULL, Budget int NOT NULL)");
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IX_BudgetHistory_F_S_From ON BudgetHistory (FactoryId, SectionId, EffectiveFrom)");
        }
        if (!lite) return; // the Entries.Budget upgrade below only applies to older SQLite files
        if (!HasColumn(db, "Entries", "Budget"))
        {
            // Existing rows take the master budget that applied so far.
            db.Database.ExecuteSqlRaw("ALTER TABLE Entries ADD COLUMN Budget INTEGER NOT NULL DEFAULT 0");
            db.Database.ExecuteSqlRaw("UPDATE Entries SET Budget = COALESCE((SELECT b.Budget FROM Budgets b WHERE b.FactoryId = Entries.FactoryId AND b.SectionId = Entries.SectionId), 0)");
        }
    }

    /// <summary>Loads the masters from the Excel sheet extract on first run.</summary>
    public static void Seed(AppDb db, string seedPath, string? adminPassword = null)
    {
        SeedCore(db, seedPath, adminPassword);
        // Every current budget needs a starting history row (applies to all earlier dates).
        db.Database.ExecuteSqlRaw("INSERT INTO BudgetHistory (FactoryId, SectionId, EffectiveFrom, Budget) SELECT b.FactoryId, b.SectionId, {0}, b.Budget FROM Budgets b WHERE NOT EXISTS (SELECT 1 FROM BudgetHistory h WHERE h.FactoryId = b.FactoryId AND h.SectionId = b.SectionId)", BudgetRates.Beginning);
    }

    static string RandomPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var s = new char[14];
        for (int i = 0; i < s.Length; i++) s[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
        s[0] = 'A'; s[1] = 'z'; s[2] = '7';   // guarantees upper, lower and digit for the password rule
        return new string(s);
    }

    static void SeedCore(AppDb db, string seedPath, string? adminPassword)
    {
        db.AuditOff = true;
        db.Database.EnsureCreated();
        UpgradeSchema(db);
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>();
        if (!db.Users.Any())
        {
            // No fixed default password: use the configured one, otherwise generate one and show it once in the console.
            var pw = string.IsNullOrWhiteSpace(adminPassword) ? RandomPassword() : adminPassword;
            var admin = new AppUser { Username = "admin", FullName = "System Administrator", Role = Role.Admin, MustChangePassword = true };
            admin.PasswordHash = hasher.HashPassword(admin, pw);
            db.Users.Add(admin);
            db.SaveChanges();
            if (string.IsNullOrWhiteSpace(adminPassword))
                Console.WriteLine($"[FIRST RUN] Admin user 'admin' created with temporary password: {pw}  (you must change it at first sign-in)");
        }
        if (db.Factories.Any() || !File.Exists(seedPath)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(seedPath));
        var root = doc.RootElement;
        int i = 0;
        var factories = new Dictionary<string, Factory>();
        foreach (var f in root.GetProperty("factories").EnumerateArray())
        {
            var fac = new Factory { Name = f.GetProperty("name").GetString()!, FullName = f.GetProperty("full").GetString(), SortOrder = ++i };
            db.Factories.Add(fac);
            factories[fac.Name] = fac;
        }
        var depts = new Dictionary<string, Department>();
        int s = 0;
        foreach (var sec in root.GetProperty("sections").EnumerateArray())
        {
            var name = sec.GetProperty("section").GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var dn = sec.GetProperty("dept").GetString()!;
            if (!depts.TryGetValue(dn, out var d))
            {
                d = new Department { Name = dn, SortOrder = depts.Count + 1, SeparateTotal = SeparateByDefault.Contains(dn) };
                depts[dn] = d;
                db.Departments.Add(d);
            }
            var section = new Section { Name = name, Department = d, SortOrder = ++s };
            db.Sections.Add(section);
            foreach (var bp in sec.GetProperty("budgets").EnumerateObject())
                if (bp.Value.GetInt32() > 0)
                    db.Budgets.Add(new BudgetMaster { Factory = factories[bp.Name], Section = section, Budget = bp.Value.GetInt32() });
        }
        db.SaveChanges();
    }
}
