using Crm.Domain.Common;
using Crm.Domain.Companies;
using Crm.Domain.Users;

namespace Crm.Domain.Accounts;

public enum AccountStatus { Prospect, Active, OnHold, Churned }
public enum AccountTier { Standard, Silver, Gold, Strategic }
public enum AccessLevel { Read = 0, Write = 1 }

/// <summary>
/// A managed customer relationship with a <see cref="Company"/>. Carries ownership and the sales team
/// that governs record-level access to everything hanging off the account.
/// </summary>
public class Account : Entity
{
    private readonly List<AccountMember> _members = [];

    private Account() { }

    public Account(Guid companyId, Guid ownerId, AccountTier tier, string? notes)
    {
        CompanyId = companyId;
        OwnerId = ownerId;
        Tier = tier;
        Notes = Guard.Optional(notes, "Notes", 4000);
        Status = AccountStatus.Prospect;
    }

    /// <summary>Database-generated sequence value backing <see cref="AccountNumber"/>.</summary>
    public int Number { get; private set; }
    public string AccountNumber => $"ACC-{Number:D6}";
    public Guid CompanyId { get; private set; }
    public Company Company { get; private set; } = null!;
    public Guid OwnerId { get; private set; }
    public User Owner { get; private set; } = null!;
    public AccountStatus Status { get; private set; }
    public AccountTier Tier { get; private set; }
    public string? Notes { get; private set; }
    public IReadOnlyCollection<AccountMember> Members => _members;

    public bool IsClosedForNewBusiness => Status is AccountStatus.Churned or AccountStatus.OnHold;

    public void Update(AccountTier tier, string? notes)
    {
        Tier = tier;
        Notes = Guard.Optional(notes, "Notes", 4000);
    }

    public void TransferOwnership(User newOwner)
    {
        if (!newOwner.CanOwnRecords)
            throw new DomainException("The new owner must be an active user with a sales role.", "invalid_owner");
        OwnerId = newOwner.Id;
        // The previous owner may remain on the team through an explicit membership; the new owner needs no row.
        _members.RemoveAll(m => m.UserId == newOwner.Id);
    }

    public void AddOrUpdateMember(User user, AccessLevel level)
    {
        if (user.Id == OwnerId)
            throw new DomainException("The account owner already has full access.", "invalid_member");
        if (!user.IsActive)
            throw new DomainException("Inactive users cannot be added to an account team.", "invalid_member");
        var existing = _members.FirstOrDefault(m => m.UserId == user.Id);
        if (existing is null) _members.Add(new AccountMember(Id, user.Id, level));
        else existing.SetLevel(level);
    }

    public bool RemoveMember(Guid userId) => _members.RemoveAll(m => m.UserId == userId) > 0;

    /// <summary>Lifecycle rules are evaluated by <see cref="Services.AccountLifecycleService"/>.</summary>
    internal void SetStatus(AccountStatus status) => Status = status;
}

public class AccountMember
{
    private AccountMember() { }

    public AccountMember(Guid accountId, Guid userId, AccessLevel level)
    {
        AccountId = accountId;
        UserId = userId;
        Level = level;
    }

    public Guid AccountId { get; private set; }
    public Guid UserId { get; private set; }
    public User User { get; private set; } = null!;
    public AccessLevel Level { get; private set; }

    public void SetLevel(AccessLevel level) => Level = level;
}
