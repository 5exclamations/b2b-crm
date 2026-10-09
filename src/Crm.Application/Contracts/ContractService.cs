using Crm.Application.Common;
using Crm.Domain.Accounts;
using Crm.Domain.Common;
using Crm.Domain.Contracts;
using Crm.Domain.Opportunities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Contracts;

public sealed record ContractDto(Guid Id, string ContractNumber, AccountSummaryDto Account, Guid? OpportunityId,
    string Title, ContractStatus Status, DateOnly StartDate, DateOnly EndDate, decimal Value, string Currency,
    bool AutoRenew, int RenewalNoticeDays, int RenewalCount, DateTimeOffset? ActivatedAt, string? TerminationReason,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateContractRequest(Guid AccountId, Guid? OpportunityId, string Title, DateOnly StartDate,
    DateOnly EndDate, decimal Value, string Currency, bool AutoRenew, int? RenewalNoticeDays);
public sealed record UpdateContractRequest(string Title, DateOnly StartDate, DateOnly EndDate, decimal Value,
    string Currency, bool AutoRenew, int? RenewalNoticeDays);
public sealed record TerminateContractRequest(string Reason);

public sealed record ContractQuery : PageRequest
{
    public string? Search { get; init; }
    public ContractStatus? Status { get; init; }
    public Guid? AccountId { get; init; }
    /// <summary>Active contracts ending within this many days from today.</summary>
    public int? ExpiringWithinDays { get; init; }
    public decimal? MinValue { get; init; }
    public decimal? MaxValue { get; init; }
}

public interface IContractService
{
    Task<PagedResult<ContractDto>> ListAsync(ContractQuery query, CancellationToken ct);
    Task<ContractDto> GetAsync(Guid id, CancellationToken ct);
    Task<ContractDto> CreateAsync(CreateContractRequest request, CancellationToken ct);
    Task<ContractDto> UpdateAsync(Guid id, UpdateContractRequest request, CancellationToken ct);
    Task<ContractDto> ActivateAsync(Guid id, CancellationToken ct);
    Task<ContractDto> TerminateAsync(Guid id, TerminateContractRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

internal sealed class ContractService(ICrmDbContext db, IAccountAccess access, ICurrentUser user,
    ITransactionRunner tx, TimeProvider clock) : IContractService
{
    internal static readonly IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<Contract, object?>>> Sorts =
        new Dictionary<string, System.Linq.Expressions.Expression<Func<Contract, object?>>>
        {
            ["number"] = c => c.Number,
            ["title"] = c => c.Title,
            ["value"] = c => c.Value,
            ["start"] = c => c.StartDate,
            ["end"] = c => c.EndDate,
            ["status"] = c => c.Status,
            ["account"] = c => c.Account.Company.NormalizedName
        };

    private static IQueryable<ContractDto> Project(IQueryable<Contract> q) => q.Select(c => new ContractDto(
        c.Id, "CTR-" + c.Number.ToString().PadLeft(6, '0'),
        new AccountSummaryDto(c.AccountId, "ACC-" + c.Account.Number.ToString().PadLeft(6, '0'), c.Account.Company.Name),
        c.OpportunityId, c.Title, c.Status, c.StartDate, c.EndDate, c.Value, c.Currency, c.AutoRenew,
        c.RenewalNoticeDays, c.RenewalCount, c.ActivatedAt, c.TerminationReason, c.CreatedAt, c.UpdatedAt));

    public async Task<PagedResult<ContractDto>> ListAsync(ContractQuery query, CancellationToken ct)
    {
        var q = db.Contracts.AsNoTracking().Where(c => access.VisibleAccountIds().Contains(c.AccountId));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            q = q.Where(c => c.Title.ToLower().Contains(term) || c.Account.Company.Name.ToLower().Contains(term)
                             || ("CTR-" + c.Number.ToString().PadLeft(6, '0')).ToLower().Contains(term));
        }
        if (query.Status is { } s) q = q.Where(c => c.Status == s);
        if (query.AccountId is { } a) q = q.Where(c => c.AccountId == a);
        if (query.MinValue is { } min) q = q.Where(c => c.Value >= min);
        if (query.MaxValue is { } max) q = q.Where(c => c.Value <= max);
        if (query.ExpiringWithinDays is { } days)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var limit = today.AddDays(days);
            q = q.Where(c => c.Status == ContractStatus.Active && c.EndDate >= today && c.EndDate <= limit);
        }
        return await Project(q.ApplySort(query.Sort, Sorts, c => c.Number, defaultDescending: true)).ToPagedAsync(query, ct);
    }

    public async Task<ContractDto> GetAsync(Guid id, CancellationToken ct) =>
        await Project(db.Contracts.AsNoTracking()
            .Where(c => c.Id == id && access.VisibleAccountIds().Contains(c.AccountId))).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Contract", id);

    public async Task<ContractDto> CreateAsync(CreateContractRequest r, CancellationToken ct)
    {
        var account = await access.GetWritableAsync(r.AccountId, ct);
        if (account.IsClosedForNewBusiness)
            throw new DomainException($"Cannot create contracts for an account that is {account.Status}.", "account_closed");

        if (r.OpportunityId is { } oppId)
        {
            var opp = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.Id == oppId, ct)
                      ?? throw new NotFoundException("Opportunity", oppId);
            if (opp.AccountId != account.Id)
                throw new DomainException("The opportunity belongs to a different account.", "invalid_opportunity");
            if (opp.Stage != OpportunityStage.ClosedWon)
                throw new DomainException("Contracts can only be created from won opportunities.", "opportunity_not_won");
            if (await db.Contracts.AnyAsync(c => c.OpportunityId == oppId && c.Status != ContractStatus.Terminated, ct))
                throw new ConflictException("A contract already exists for this opportunity.", "duplicate_contract");
        }

        var contract = new Contract(account.Id, r.OpportunityId, r.Title, r.StartDate, r.EndDate, r.Value, r.Currency,
            r.AutoRenew, r.RenewalNoticeDays ?? Contract.DefaultRenewalNoticeDays);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync(ct);
        return await GetAsync(contract.Id, ct);
    }

    public async Task<ContractDto> UpdateAsync(Guid id, UpdateContractRequest r, CancellationToken ct)
    {
        var contract = await LoadWritableAsync(id, ct);
        contract.Update(r.Title, r.StartDate, r.EndDate, r.Value, r.Currency, r.AutoRenew,
            r.RenewalNoticeDays ?? Contract.DefaultRenewalNoticeDays);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ContractDto> ActivateAsync(Guid id, CancellationToken ct)
    {
        EnsureCanApprove();
        await tx.ExecuteAsync(async token =>
        {
            var contract = await LoadWritableAsync(id, token);
            var account = await db.Accounts.FirstAsync(a => a.Id == contract.AccountId, token);
            if (account.Status != AccountStatus.Active)
                throw new DomainException("Contracts can only be activated for active accounts.", "account_not_active");
            var now = clock.GetUtcNow();
            contract.Activate(now, DateOnly.FromDateTime(now.UtcDateTime));
            await db.SaveChangesAsync(token);
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<ContractDto> TerminateAsync(Guid id, TerminateContractRequest r, CancellationToken ct)
    {
        EnsureCanApprove();
        var contract = await LoadWritableAsync(id, ct);
        contract.Terminate(r.Reason);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var contract = await LoadWritableAsync(id, ct);
        if (contract.Status != ContractStatus.Draft)
            throw new DomainException("Only draft contracts can be deleted.", "contract_not_editable");
        db.Contracts.Remove(contract);
        await db.SaveChangesAsync(ct);
    }

    private void EnsureCanApprove()
    {
        if (!user.Has(Permissions.ContractsApprove))
            throw new ForbiddenException("Only managers can activate or terminate contracts.");
    }

    private async Task<Contract> LoadWritableAsync(Guid id, CancellationToken ct)
    {
        var contract = await db.Contracts.FirstOrDefaultAsync(c => c.Id == id, ct)
                       ?? throw new NotFoundException("Contract", id);
        await access.GetWritableAsync(contract.AccountId, ct);
        return contract;
    }
}

public sealed class CreateContractRequestValidator : AbstractValidator<CreateContractRequest>
{
    public CreateContractRequestValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate).WithMessage("End date must be after the start date.");
        RuleFor(x => x.Value).GreaterThan(0).PrecisionScale(18, 2, false);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.RenewalNoticeDays).InclusiveBetween(0, 365).When(x => x.RenewalNoticeDays.HasValue);
    }
}

public sealed class UpdateContractRequestValidator : AbstractValidator<UpdateContractRequest>
{
    public UpdateContractRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate).WithMessage("End date must be after the start date.");
        RuleFor(x => x.Value).GreaterThan(0).PrecisionScale(18, 2, false);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.RenewalNoticeDays).InclusiveBetween(0, 365).When(x => x.RenewalNoticeDays.HasValue);
    }
}

public sealed class TerminateContractRequestValidator : AbstractValidator<TerminateContractRequest>
{
    public TerminateContractRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
}

public sealed class ContractQueryValidator : AbstractValidator<ContractQuery>
{
    public ContractQueryValidator()
    {
        Include(new PageRequestValidator<ContractQuery>(ContractService.Sorts.Keys));
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.ExpiringWithinDays).InclusiveBetween(0, 3650).When(x => x.ExpiringWithinDays.HasValue);
    }
}
