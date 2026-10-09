using Crm.Domain.Accounts;
using Crm.Domain.Users;

namespace Crm.Domain.Services;

/// <summary>
/// Record-level authorization for accounts (and every record that belongs to one).
/// Complements role-based permissions, which are enforced at the API boundary.
/// </summary>
public static class AccountAccessPolicy
{
    public static bool HasGlobalReadAccess(UserRole role) => role != UserRole.SalesRep;
    public static bool HasGlobalWriteAccess(UserRole role) => role is UserRole.Admin or UserRole.SalesManager;

    public static bool CanRead(UserRole role, Guid userId, Account account) =>
        HasGlobalReadAccess(role) || account.OwnerId == userId || account.Members.Any(m => m.UserId == userId);

    public static bool CanWrite(UserRole role, Guid userId, Account account)
    {
        if (role == UserRole.ReadOnly) return false;
        if (HasGlobalWriteAccess(role)) return true;
        return account.OwnerId == userId
               || account.Members.Any(m => m.UserId == userId && m.Level == AccessLevel.Write);
    }
}
