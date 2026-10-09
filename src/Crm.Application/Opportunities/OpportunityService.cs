using Crm.Application.Common;
using Crm.Domain.Accounts;
using Crm.Domain.Activities;
using Crm.Domain.Common;
using Crm.Domain.Opportunities;
using Crm.Domain.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Opportunities;

public sealed record OpportunityDto(Guid Id, AccountSummaryDto Account, UserSummaryDto Owner, Guid? PrimaryContactId,
    string Name, string? Description, OpportunityStage Stage, decimal Amount, string Currency, int Probability,
    decimal WeightedAmount, DateOnly ExpectedCloseDate, DateOnly? ActualCloseDate, string? LossReason,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateOpportunityRequest(Guid AccountId, string Name, string? Description, decimal Amount,
    string Currency, DateOnly ExpectedCloseDate, Guid? PrimaryContactId);
public sealed record UpdateOpportunityRequest(string Name, string? Description, decimal Amount, string Currency,
    DateOnly ExpectedCloseDate, Guid? PrimaryContactId);
public sealed record ChangeStageRequest(OpportunityStage Stage, string? LossReason);
public sealed record ReassignRequest(Guid NewOwnerId);

public sealed record OpportunityQuery : PageRequest
{
    public string? Search { get; init; }
    public OpportunityStage? Stage { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? OwnerId { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public DateOnly? CloseFrom { get; init; }
    public DateOnly? CloseTo { get; init; }
    public bool? OnlyOpen { get; init; }
}

public interface IOpportunityService
{
    Task<PagedResult<OpportunityDto>> ListAsync(OpportunityQuery query, CancellationToken ct);
    Task<OpportunityDto> GetAsync(Guid id, CancellationToken ct);
    Task<OpportunityDto> CreateAsync(CreateOpportunityRequest request, CancellationToken ct);
    Task<OpportunityDto> UpdateAsync(Guid id, UpdateOpportunityRequest request, CancellationToken ct);
    Task<OpportunityDto> ChangeStageAsync(Guid id, ChangeStageRequest request, CancellationToken ct);
    Task<OpportunityDto> ReassignAsync(Guid id, ReassignRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

internal sealed class OpportunityService(ICrmDbContext db, IAccountAccess access, ICurrentUser user,
    ITransactionRunner tx, TimeProvider clock) : IOpportunityService
{
    internal static readonly IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<Opportunity, object?>>> Sorts =
        new Dictionary<string, System.Linq.Expressions.Expression<Func<Opportunity, object?>>>
        {
            ["name"] = o => o.Name,
            ["amount"] = o => o.Amount,
            ["stage"] = o => o.Stage,
            ["closedate"] = o => o.ExpectedCloseDate,
            ["account"] = o => o.Account.Company.NormalizedName,
            ["created"] = o => o.CreatedAt
        };

    private IQueryable<OpportunityDto> Project(IQueryable<Opportunity> q) => q.Select(o => new OpportunityDto(
        o.Id,
        new AccountSummaryDto(o.AccountId, "ACC-" + o.Account.Number.ToString().PadLeft(6, '0'), o.Account.Company.Name),
        new UserSummaryDto(o.OwnerId, db.Users.Where(u => u.Id == o.OwnerId).Select(u => u.FullName).FirstOrDefault()!),
        o.PrimaryContactId, o.Name, o.Description, o.Stage, o.Amount, o.Currency, o.Probability,
        Math.Round(o.Amount * o.Probability / 100m, 2), o.ExpectedCloseDate, o.ActualCloseDate, o.LossReason,
        o.CreatedAt, o.UpdatedAt));

    public async Task<PagedResult<OpportunityDto>> ListAsync(OpportunityQuery query, CancellationToken ct)
    {
        var q = db.Opportunities.AsNoTracking().Where(o => access.VisibleAccountIds().Contains(o.AccountId));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            q = q.Where(o => o.Name.ToLower().Contains(term) || o.Account.Company.Name.ToLower().Contains(term)
                             || (o.Description != null && o.Description.ToLower().Contains(term)));
        }
        if (query.Stage is { } s) q = q.Where(o => o.Stage == s);
        if (query.AccountId is { } a) q = q.Where(o => o.AccountId == a);
        if (query.OwnerId is { } ow) q = q.Where(o => o.OwnerId == ow);
        if (query.MinAmount is { } min) q = q.Where(o => o.Amount >= min);
        if (query.MaxAmount is { } max) q = q.Where(o => o.Amount <= max);
        if (query.CloseFrom is { } from) q = q.Where(o => o.ExpectedCloseDate >= from);
        if (query.CloseTo is { } to) q = q.Where(o => o.ExpectedCloseDate <= to);
        if (query.OnlyOpen == true)
            q = q.Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost);

        return await Project(q.ApplySort(query.Sort, Sorts, o => o.CreatedAt, defaultDescending: true)).ToPagedAsync(query, ct);
    }

    public async Task<OpportunityDto> GetAsync(Guid id, CancellationToken ct)
    {
        var dto = await Project(db.Opportunities.AsNoTracking()
            .Where(o => o.Id == id && access.VisibleAccountIds().Contains(o.AccountId))).FirstOrDefaultAsync(ct);
        return dto ?? throw new NotFoundException("Opportunity", id);
    }

    public async Task<OpportunityDto> CreateAsync(CreateOpportunityRequest r, CancellationToken ct)
    {
        var account = await access.GetWritableAsync(r.AccountId, ct);
        if (account.IsClosedForNewBusiness)
            throw new DomainException($"Cannot create opportunities for an account that is {account.Status}.", "account_closed");
        await EnsureContactBelongsAsync(account, r.PrimaryContactId, ct);

        var opportunity = new Opportunity(account.Id, user.RequireId(), r.Name, r.Description, r.Amount, r.Currency,
            r.ExpectedCloseDate, r.PrimaryContactId);
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync(ct);
        return await GetAsync(opportunity.Id, ct);
    }

    public async Task<OpportunityDto> UpdateAsync(Guid id, UpdateOpportunityRequest r, CancellationToken ct)
    {
        var (opportunity, account) = await LoadWritableAsync(id, ct);
        await EnsureContactBelongsAsync(account, r.PrimaryContactId, ct);
        opportunity.Update(r.Name, r.Description, r.Amount, r.Currency, r.ExpectedCloseDate, r.PrimaryContactId);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Stage changes can trigger side effects on other aggregates, so they run in one transaction: winning a deal
    /// activates a prospect account and schedules an onboarding task for the owner.
    /// </summary>
    public async Task<OpportunityDto> ChangeStageAsync(Guid id, ChangeStageRequest r, CancellationToken ct)
    {
        await tx.ExecuteAsync(async token =>
        {
            var (opportunity, account) = await LoadWritableAsync(id, token);
            var now = clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            opportunity.ChangeStage(r.Stage, r.LossReason, today);

            if (opportunity.Stage == OpportunityStage.ClosedWon)
            {
                if (account.Status == AccountStatus.Prospect)
                    AccountLifecycleService.ChangeStatus(account, AccountStatus.Active, default, user.IsManager());
                db.Tasks.Add(new WorkTask(account.Id, opportunity.OwnerId, $"Onboard customer: {opportunity.Name}",
                    "Opportunity won - kick off onboarding and prepare the contract.", today.AddDays(7),
                    TaskPriority.High, opportunity.Id, today));
            }
            await db.SaveChangesAsync(token);
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<OpportunityDto> ReassignAsync(Guid id, ReassignRequest r, CancellationToken ct)
    {
        if (!user.Has(Permissions.AccountsReassign))
            throw new ForbiddenException("Only managers can reassign opportunities.");
        var (opportunity, _) = await LoadWritableAsync(id, ct);
        var owner = await db.Users.FirstOrDefaultAsync(u => u.Id == r.NewOwnerId, ct)
                    ?? throw new NotFoundException("User", r.NewOwnerId);
        if (!owner.CanOwnRecords)
            throw new DomainException("The new owner must be an active user with a sales role.", "invalid_owner");
        opportunity.Reassign(owner.Id);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var (opportunity, _) = await LoadWritableAsync(id, ct);
        if (opportunity.Stage == OpportunityStage.ClosedWon)
            throw new DomainException("Won opportunities are part of the sales record and cannot be deleted.", "opportunity_closed");
        if (await db.Contracts.AnyAsync(c => c.OpportunityId == id, ct))
            throw new ConflictException("The opportunity is referenced by a contract.", "opportunity_in_use");
        db.Opportunities.Remove(opportunity);
        await db.SaveChangesAsync(ct);
    }

    private async Task<(Opportunity, Account)> LoadWritableAsync(Guid id, CancellationToken ct)
    {
        var opportunity = await db.Opportunities.FirstOrDefaultAsync(o => o.Id == id, ct)
                          ?? throw new NotFoundException("Opportunity", id);
        var account = await access.GetWritableAsync(opportunity.AccountId, ct);
        return (opportunity, account);
    }

    private async Task EnsureContactBelongsAsync(Account account, Guid? contactId, CancellationToken ct)
    {
        if (contactId is null) return;
        if (!await db.Contacts.AnyAsync(c => c.Id == contactId && c.CompanyId == account.CompanyId, ct))
            throw new DomainException("The contact does not belong to the account's company.", "invalid_contact");
    }
}

public sealed class CreateOpportunityRequestValidator : AbstractValidator<CreateOpportunityRequest>
{
    public CreateOpportunityRequestValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(4000);
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(18, 2, false);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
    }
}

public sealed class UpdateOpportunityRequestValidator : AbstractValidator<UpdateOpportunityRequest>
{
    public UpdateOpportunityRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(4000);
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(18, 2, false);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
    }
}

public sealed class ChangeStageRequestValidator : AbstractValidator<ChangeStageRequest>
{
    public ChangeStageRequestValidator()
    {
        RuleFor(x => x.Stage).IsInEnum();
        RuleFor(x => x.LossReason).NotEmpty().When(x => x.Stage == OpportunityStage.ClosedLost)
            .WithMessage("A loss reason is required when closing an opportunity as lost.");
        RuleFor(x => x.LossReason).MaximumLength(500);
    }
}

public sealed class ReassignRequestValidator : AbstractValidator<ReassignRequest>
{
    public ReassignRequestValidator() => RuleFor(x => x.NewOwnerId).NotEmpty();
}

public sealed class OpportunityQueryValidator : AbstractValidator<OpportunityQuery>
{
    public OpportunityQueryValidator()
    {
        Include(new PageRequestValidator<OpportunityQuery>(OpportunityService.Sorts.Keys));
        RuleFor(x => x.Stage).IsInEnum().When(x => x.Stage.HasValue);
        RuleFor(x => x).Must(x => x.MinAmount is null || x.MaxAmount is null || x.MinAmount <= x.MaxAmount)
            .WithMessage("MinAmount must not exceed MaxAmount.");
        RuleFor(x => x).Must(x => x.CloseFrom is null || x.CloseTo is null || x.CloseFrom <= x.CloseTo)
            .WithMessage("CloseFrom must not be after CloseTo.");
    }
}
