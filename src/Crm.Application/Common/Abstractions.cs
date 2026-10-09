using Crm.Domain.Accounts;
using Crm.Domain.Activities;
using Crm.Domain.Audit;
using Crm.Domain.Companies;
using Crm.Domain.Contracts;
using Crm.Domain.Opportunities;
using Crm.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Common;

/// <summary>Persistence abstraction the application layer programs against (implemented by the EF Core context).</summary>
public interface ICrmDbContext
{
    DbSet<User> Users { get; }
    DbSet<Company> Companies { get; }
    DbSet<Contact> Contacts { get; }
    DbSet<Account> Accounts { get; }
    DbSet<AccountMember> AccountMembers { get; }
    DbSet<Opportunity> Opportunities { get; }
    DbSet<Contract> Contracts { get; }
    DbSet<Activity> Activities { get; }
    DbSet<WorkTask> Tasks { get; }
    DbSet<AuditLog> AuditLogs { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Defines an explicit transaction boundary. Nested calls join the ambient transaction.</summary>
public interface ITransactionRunner
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        ExecuteAsync<bool>(async ct => { await action(ct); return true; }, cancellationToken);
}

/// <summary>Cross-instance mutual exclusion for background jobs.</summary>
public interface IJobLock
{
    /// <summary>Returns a handle that releases the lock when disposed, or null if another instance holds it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken);
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? Email { get; }
    UserRole? Role { get; }
    string? CorrelationId { get; }
}

public static class CurrentUserExtensions
{
    public static Guid RequireId(this ICurrentUser user) =>
        user.UserId ?? throw new ForbiddenException("Authentication is required.");

    public static UserRole RequireRole(this ICurrentUser user) =>
        user.Role ?? throw new ForbiddenException("Authentication is required.");

    public static bool IsManager(this ICurrentUser user) => user.Role is UserRole.Admin or UserRole.SalesManager;
    public static bool Has(this ICurrentUser user, string permission) =>
        user.Role is { } role && Crm.Domain.Permissions.Has(role, permission);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IAccessTokenIssuer
{
    AccessToken Issue(User user);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);
