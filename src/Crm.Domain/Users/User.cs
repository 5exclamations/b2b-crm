using Crm.Domain.Common;

namespace Crm.Domain.Users;

public enum UserRole { ReadOnly = 0, SalesRep = 1, SalesManager = 2, Admin = 3 }

public class User : Entity
{
    private User() { }

    public User(string email, string fullName, string passwordHash, UserRole role)
    {
        Email = NormalizeEmail(email);
        FullName = Guard.Required(fullName, "Full name", 200);
        PasswordHash = passwordHash;
        Role = role;
        IsActive = true;
    }

    public string Email { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public UserRole Role { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>Users that may own accounts, opportunities and tasks.</summary>
    public bool CanOwnRecords => IsActive && Role >= UserRole.SalesRep;

    public static string NormalizeEmail(string? email) => Guard.Required(email, "Email", 320).ToLowerInvariant();

    public void UpdateProfile(string fullName, UserRole role)
    {
        FullName = Guard.Required(fullName, "Full name", 200);
        Role = role;
    }

    public void SetActive(bool active) => IsActive = active;
    public void ChangePasswordHash(string hash) => PasswordHash = hash;
    public void RecordLogin(DateTimeOffset at) => LastLoginAt = at;
}
