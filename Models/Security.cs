using System.ComponentModel.DataAnnotations;

namespace HrProject_Budget.Models;

public enum Role { Admin, SuperUser, User }

public class AppUser
{
    public int Id { get; set; }
    [Required, StringLength(50)] public string Username { get; set; } = "";
    [Required, StringLength(100), Display(Name = "Full name")] public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public Role Role { get; set; } = Role.User;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>true = may work with every factory; false = only the factories listed in Factories.</summary>
    public bool AllFactories { get; set; } = true;
    /// <summary>true = must choose a new password at next sign-in (first admin, admin reset).</summary>
    public bool MustChangePassword { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public List<UserPermission> Permissions { get; set; } = new();
    public List<UserFactory> Factories { get; set; } = new();
}

/// <summary>A row means the user may open the screen; CanEdit allows saving changes.</summary>
public class UserPermission
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    [StringLength(40)] public string Screen { get; set; } = "";
    public bool CanEdit { get; set; }
}

public class PermRow
{
    public string Screen { get; set; } = "";
    public string Label { get; set; } = "";
    public bool View { get; set; }
    public bool Edit { get; set; }
}

public class UserEditModel
{
    public int Id { get; set; }
    [Required, StringLength(50)] public string Username { get; set; } = "";
    [Required, StringLength(100), Display(Name = "Full name")] public string FullName { get; set; } = "";
    public Role Role { get; set; } = Role.User;
    public bool IsActive { get; set; } = true;
    [Display(Name = "All factories")] public bool AllFactories { get; set; } = true;
    public List<int> FactoryIds { get; set; } = new();
    [DataType(DataType.Password), Display(Name = "Password")] public string? Password { get; set; }
    public List<PermRow> Perms { get; set; } = new();
}

public class LoginModel
{
    [Required] public string Username { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class ChangePasswordModel
{
    [Required, DataType(DataType.Password), Display(Name = "Current password")] public string Current { get; set; } = "";
    [Required, DataType(DataType.Password), Display(Name = "New password")] public string New { get; set; } = "";
    [Required, DataType(DataType.Password), Compare(nameof(New)), Display(Name = "Confirm new password")] public string Confirm { get; set; } = "";
}

public static class PasswordPolicy
{
    public const string Rule = "Use at least 8 characters with an upper-case letter, a lower-case letter and a number.";

    /// <summary>Returns an error message, or null when the password is acceptable.</summary>
    public static string? Check(string? p) =>
        p != null && p.Length >= 8 && p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit) ? null : Rule;
}

