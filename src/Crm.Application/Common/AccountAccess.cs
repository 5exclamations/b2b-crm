using Crm.Domain.Accounts;
using Crm.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Common;

/// <summary>Loads accounts enforcing record-level access, and exposes the visibility scope for list queries.</summary>
public interface IAccountAccess
{
    /// <summary>Loads the account (with team) or throws 404 when it does not exist or is not visible to the caller.</summary>
    Task<Account> GetReadableAsync(Guid accountId, CancellationToken ct);
    /// <summary>As <see cref="GetReadableAsync"/> but additionally throws 403 if the caller may not modify it.</summary>
    Task<Account> GetWritableAsync(Guid accountId, CancellationToken ct);
    /// <summary>Ids of accounts the caller may read; use as a sub-query to scope child records.</summary>
    IQueryable<Guid> VisibleAccountIds();
}

internal sealed class AccountAccess(ICrmDbContext db, ICurrentUser user) : IAccountAccess
{
    public async Task<Account> GetReadableAsync(Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == accountId, ct)
                      ?? throw new NotFoundException("Account", accountId);
        if (!AccountAccessPolicy.CanRead(user.RequireRole(), user.RequireId(), account))
            throw new NotFoundException("Account", accountId); // do not disclose existence
        return account;
    }

    public async Task<Account> GetWritableAsync(Guid accountId, CancellationToken ct)
    {
        var account = await GetReadableAsync(accountId, ct);
        if (!AccountAccessPolicy.CanWrite(user.RequireRole(), user.RequireId(), account))
            throw new ForbiddenException("You do not have write access to this account.");
        return account;
    }

    public IQueryable<Guid> VisibleAccountIds()
    {
        var accounts = db.Accounts.AsQueryable();
        if (!AccountAccessPolicy.HasGlobalReadAccess(user.RequireRole()))
        {
            var id = user.RequireId();
            accounts = accounts.Where(a => a.OwnerId == id || a.Members.Any(m => m.UserId == id));
        }
        return accounts.Select(a => a.Id);
    }
}
