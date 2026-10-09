using Crm.Application.Common;
using Crm.Domain.Accounts;
using Crm.Domain.Activities;
using Crm.Domain.Common;
using Crm.Domain.Contracts;
using Crm.Domain.Opportunities;
using Crm.Domain.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Accounts;

public sealed record AccountMemberDto(Guid UserId, string FullName, AccessLevel Level);

public sealed record AccountDto(Guid Id, string AccountNumber, CompanySummaryDto Company, UserSummaryDto Owner,
    AccountStatus Status, AccountTier Tier, string? Industry, string? Country, string? Notes,
    IReadOnlyList<AccountMemberDto> Members, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateAccountRequest(Guid CompanyId, Guid? OwnerId, AccountTier Tier, string? Notes);
public sealed record UpdateAccountRequest(AccountTier Tier, string? Notes);
public sealed record ChangeAccountStatusRequest(AccountStatus Status);
public sealed record TransferOwnershipRequest(Guid NewOwnerId, string? Reason);
public sealed record SetMemberRequest(AccessLevel Level);

public sealed record AccountQuery : PageRequest
{
    public string? Search { get; init; }
    public AccountStatus? Status { get; init; }
    public AccountTier? Tier { get; init; }
    public Guid? OwnerId { get; init; }
    public string? Industry { get; init; }
    public string? Country { get; init; }
    /// <summary>When true only accounts owned by or shared with the caller are returned.</summary>
    public bool? Mine { get; init; }
}

public interface IAccountService
{
    Task<PagedResult<AccountDto>> ListAsync(AccountQuery query, CancellationToken ct);
    Task<AccountDto> GetAsync(Guid id, CancellationToken ct);
    Task<AccountDto> CreateAsync(CreateAccountRequest request, CancellationToken ct);
    Task<AccountDto> UpdateAsync(Guid id, UpdateAccountRequest request, CancellationToken ct);
    Task<AccountDto> ChangeStatusAsync(Guid id, ChangeAccountStatusRequest request, CancellationToken ct);
    Task<AccountDto> TransferOwnershipAsync(Guid id, TransferOwnershipRequest request, CancellationToken ct);
    Task<AccountDto> SetMemberAsync(Guid id, Guid userId, SetMemberRequest request, CancellationToken ct);
    Task<AccountDto> RemoveMemberAsync(Guid id, Guid userId, CancellationToken ct);
}

internal sealed class AccountService(ICrmDbContext db, IAccountAccess access, ICurrentUser user,
    ITransactionRunner tx, TimeProvider clock) : IAccountService
{
    internal static readonly IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<Account, object?>>> Sorts =
        new Dictionary<string, System.Linq.Expressions.Expression<Func<Account, object?>>>
        {
            ["name"] = a => a.Company.NormalizedName,
            ["number"] = a => a.Number,
            ["status"] = a => a.Status,
            ["tier"] = a => a.Tier,
            ["owner"] = a => a.Owner.FullName,
            ["created"] = a => a.CreatedAt
        };

    private static IQueryable<AccountDto> Project(IQueryable<Account> q) => q.Select(a => new AccountDto(
        a.Id, "ACC-" + a.Number.ToString().PadLeft(6, '0'),
        new CompanySummaryDto(a.CompanyId, a.Company.Name),
        new UserSummaryDto(a.OwnerId, a.Owner.FullName),
        a.Status, a.Tier, a.Company.Industry, a.Company.Country, a.Notes,
        a.Members.Select(m => new AccountMemberDto(m.UserId, m.User.FullName, m.Level)).ToList(),
        a.CreatedAt, a.UpdatedAt));

    public async Task<PagedResult<AccountDto>> ListAsync(AccountQuery query, CancellationToken ct)
    {
        var q = db.Accounts.AsNoTracking().Where(a => access.VisibleAccountIds().Contains(a.Id));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            q = q.Where(a => a.Company.NormalizedName.Contains(term)
                             || (a.Company.LegalName != null && a.Company.LegalName.ToUpper().Contains(term))
                             || (a.Company.City != null && a.Company.City.ToUpper().Contains(term))
                             || a.Owner.FullName.ToUpper().Contains(term)
                             || ("ACC-" + a.Number.ToString().PadLeft(6, '0')).Contains(term));
        }
        if (query.Status is { } s) q = q.Where(a => a.Status == s);
        if (query.Tier is { } t) q = q.Where(a => a.Tier == t);
        if (query.OwnerId is { } o) q = q.Where(a => a.OwnerId == o);
        if (!string.IsNullOrWhiteSpace(query.Industry)) q = q.Where(a => a.Company.Industry == query.Industry);
        if (!string.IsNullOrWhiteSpace(query.Country)) q = q.Where(a => a.Company.Country == query.Country);
        if (query.Mine == true)
        {
            var me = user.RequireId();
            q = q.Where(a => a.OwnerId == me || a.Members.Any(m => m.UserId == me));
        }
        return await Project(q.ApplySort(query.Sort, Sorts, a => a.Company.NormalizedName)).ToPagedAsync(query, ct);
    }

    public async Task<AccountDto> GetAsync(Guid id, CancellationToken ct)
    {
        await access.GetReadableAsync(id, ct);
        return await LoadDtoAsync(id, ct);
    }

    public async Task<AccountDto> CreateAsync(CreateAccountRequest r, CancellationToken ct)
    {
        var me = user.RequireId();
        var ownerId = r.OwnerId ?? me;
        if (ownerId != me && !user.Has(Permissions.AccountsReassign))
            throw new ForbiddenException("Only managers can create accounts on behalf of another owner.");

        var id = await tx.ExecuteAsync(async token =>
        {
            if (!await db.Companies.AnyAsync(c => c.Id == r.CompanyId, token))
                throw new NotFoundException("Company", r.CompanyId);
            if (await db.Accounts.AnyAsync(a => a.CompanyId == r.CompanyId, token))
                throw new ConflictException("This company already has a customer account.", "duplicate_account");
            var owner = await db.Users.FirstOrDefaultAsync(u => u.Id == ownerId, token)
                        ?? throw new NotFoundException("User", ownerId);
            if (!owner.CanOwnRecords)
                throw new DomainException("The owner must be an active user with a sales role.", "invalid_owner");

            var account = new Account(r.CompanyId, ownerId, r.Tier, r.Notes);
            db.Accounts.Add(account);
            await db.SaveChangesAsync(token);
            return account.Id;
        }, ct);
        return await LoadDtoAsync(id, ct);
    }

    public async Task<AccountDto> UpdateAsync(Guid id, UpdateAccountRequest r, CancellationToken ct)
    {
        var account = await access.GetWritableAsync(id, ct);
        account.Update(r.Tier, r.Notes);
        await db.SaveChangesAsync(ct);
        return await LoadDtoAsync(id, ct);
    }

    public async Task<AccountDto> ChangeStatusAsync(Guid id, ChangeAccountStatusRequest r, CancellationToken ct)
    {
        await tx.ExecuteAsync(async token =>
        {
            var account = await access.GetWritableAsync(id, token);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var activity = new AccountActivity(
                await db.Contracts.CountAsync(c => c.AccountId == id && c.Status == ContractStatus.Active, token),
                await db.Opportunities.CountAsync(o => o.AccountId == id
                    && o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost, token));
            AccountLifecycleService.ChangeStatus(account, r.Status, activity, user.IsManager());
            await db.SaveChangesAsync(token);
        }, ct);
        return await LoadDtoAsync(id, ct);
    }

    /// <summary>
    /// Moves an account to a new owner. One transaction covers the account, its open opportunities and open tasks
    /// (so nothing is left assigned to the previous owner) and a note recording the hand-over.
    /// </summary>
    public async Task<AccountDto> TransferOwnershipAsync(Guid id, TransferOwnershipRequest r, CancellationToken ct)
    {
        if (!user.Has(Permissions.AccountsReassign))
            throw new ForbiddenException("Only managers can transfer account ownership.");

        await tx.ExecuteAsync(async token =>
        {
            var account = await access.GetWritableAsync(id, token);
            if (account.OwnerId == r.NewOwnerId)
                throw new DomainException("The account is already owned by this user.", "invalid_owner");
            var newOwner = await db.Users.FirstOrDefaultAsync(u => u.Id == r.NewOwnerId, token)
                           ?? throw new NotFoundException("User", r.NewOwnerId);
            var previousOwnerId = account.OwnerId;

            account.TransferOwnership(newOwner);

            var openOpportunities = await db.Opportunities
                .Where(o => o.AccountId == id && o.OwnerId == previousOwnerId
                            && o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost)
                .ToListAsync(token);
            foreach (var o in openOpportunities) o.Reassign(newOwner.Id);

            var openTasks = await db.Tasks
                .Where(t => t.AccountId == id && t.AssignedToId == previousOwnerId
                            && t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled)
                .ToListAsync(token);
            foreach (var t in openTasks) t.Reassign(newOwner.Id);

            var note = string.IsNullOrWhiteSpace(r.Reason) ? "" : $" Reason: {r.Reason.Trim()}";
            db.Activities.Add(new Activity(id, user.RequireId(), ActivityType.Note, "Account ownership transferred",
                $"Ownership moved to {newOwner.FullName}; {openOpportunities.Count} opportunities and {openTasks.Count} tasks reassigned.{note}",
                clock.GetUtcNow(), null, null, null, clock.GetUtcNow()));
            await db.SaveChangesAsync(token);
        }, ct);
        return await LoadDtoAsync(id, ct);
    }

    public async Task<AccountDto> SetMemberAsync(Guid id, Guid userId, SetMemberRequest r, CancellationToken ct)
    {
        var account = await access.GetWritableAsync(id, ct);
        var member = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                     ?? throw new NotFoundException("User", userId);
        if (member.Role < Crm.Domain.Users.UserRole.SalesRep && r.Level == AccessLevel.Write)
            throw new DomainException("Read-only users cannot be granted write access.", "invalid_member");
        account.AddOrUpdateMember(member, r.Level);
        await db.SaveChangesAsync(ct);
        return await LoadDtoAsync(id, ct);
    }

    public async Task<AccountDto> RemoveMemberAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var account = await access.GetWritableAsync(id, ct);
        if (!account.RemoveMember(userId)) throw new NotFoundException("Account member", userId);
        await db.SaveChangesAsync(ct);
        return await LoadDtoAsync(id, ct);
    }

    private async Task<AccountDto> LoadDtoAsync(Guid id, CancellationToken ct) =>
        await Project(db.Accounts.AsNoTracking().Where(a => a.Id == id)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Account", id);
}

public sealed class CreateAccountRequestValidator : AbstractValidator<CreateAccountRequest>
{
    public CreateAccountRequestValidator()
    {
        RuleFor(x => x.CompanyId).NotEmpty();
        RuleFor(x => x.Tier).IsInEnum();
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}

public sealed class UpdateAccountRequestValidator : AbstractValidator<UpdateAccountRequest>
{
    public UpdateAccountRequestValidator()
    {
        RuleFor(x => x.Tier).IsInEnum();
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}

public sealed class ChangeAccountStatusRequestValidator : AbstractValidator<ChangeAccountStatusRequest>
{
    public ChangeAccountStatusRequestValidator() => RuleFor(x => x.Status).IsInEnum();
}

public sealed class TransferOwnershipRequestValidator : AbstractValidator<TransferOwnershipRequest>
{
    public TransferOwnershipRequestValidator()
    {
        RuleFor(x => x.NewOwnerId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class SetMemberRequestValidator : AbstractValidator<SetMemberRequest>
{
    public SetMemberRequestValidator() => RuleFor(x => x.Level).IsInEnum();
}

public sealed class AccountQueryValidator : AbstractValidator<AccountQuery>
{
    public AccountQueryValidator()
    {
        Include(new PageRequestValidator<AccountQuery>(AccountService.Sorts.Keys));
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.Tier).IsInEnum().When(x => x.Tier.HasValue);
    }
}
