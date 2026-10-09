using Crm.Application.Common;
using Crm.Domain.Accounts;
using Crm.Domain.Activities;
using Crm.Domain.Contracts;
using Crm.Domain.Opportunities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Reports;

public sealed record PipelineStageRow(OpportunityStage Stage, int Count, decimal TotalAmount, decimal WeightedAmount);
public sealed record PipelineReport(string Currency, IReadOnlyList<PipelineStageRow> Stages, int OpenCount,
    decimal OpenAmount, decimal OpenWeightedAmount);

public sealed record SalesPerformanceRow(Guid OwnerId, string OwnerName, int WonCount, decimal WonAmount, int LostCount,
    decimal? WinRatePercent, int OpenCount, decimal OpenAmount);
public sealed record SalesPerformanceReport(string Currency, DateOnly From, DateOnly To,
    IReadOnlyList<SalesPerformanceRow> Owners, int TotalWonCount, decimal TotalWonAmount);

public sealed record CountBy<T>(T Key, int Count);
public sealed record AccountsSummaryReport(int Total, IReadOnlyList<CountBy<AccountStatus>> ByStatus,
    IReadOnlyList<CountBy<AccountTier>> ByTier);

public sealed record ExpiringContractRow(Guid ContractId, string ContractNumber, string Title, Guid AccountId,
    string CompanyName, DateOnly EndDate, int DaysRemaining, decimal Value, string Currency, bool AutoRenew);
public sealed record ExpiringContractsReport(int WithinDays, int Count, IReadOnlyList<ExpiringContractRow> Contracts);

public sealed record OverdueByAssignee(Guid UserId, string FullName, int OverdueCount);
public sealed record TaskSummaryReport(IReadOnlyList<CountBy<WorkTaskStatus>> ByStatus, int OverdueCount,
    IReadOnlyList<OverdueByAssignee> OverdueByAssignee);

public sealed record ActivitySummaryReport(DateTimeOffset From, DateTimeOffset To, int Total,
    IReadOnlyList<CountBy<ActivityType>> ByType, IReadOnlyList<CountBy<string>> ByUser);

public sealed record ReportRange
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string Currency { get; init; } = "USD";
    public Guid? OwnerId { get; init; }
}

public sealed record ActivityRange
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

public interface IReportService
{
    Task<PipelineReport> PipelineAsync(ReportRange range, CancellationToken ct);
    Task<SalesPerformanceReport> SalesPerformanceAsync(ReportRange range, CancellationToken ct);
    Task<AccountsSummaryReport> AccountsSummaryAsync(CancellationToken ct);
    Task<ExpiringContractsReport> ExpiringContractsAsync(int days, CancellationToken ct);
    Task<TaskSummaryReport> TaskSummaryAsync(CancellationToken ct);
    Task<ActivitySummaryReport> ActivitySummaryAsync(ActivityRange range, CancellationToken ct);
}

/// <summary>All reports are computed in the database and honour the caller's account visibility.</summary>
internal sealed class ReportService(ICrmDbContext db, IAccountAccess access, TimeProvider clock) : IReportService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<PipelineReport> PipelineAsync(ReportRange range, CancellationToken ct)
    {
        var currency = range.Currency.ToUpperInvariant();
        var q = db.Opportunities.AsNoTracking()
            .Where(o => access.VisibleAccountIds().Contains(o.AccountId) && o.Currency == currency);
        if (range.OwnerId is { } owner) q = q.Where(o => o.OwnerId == owner);
        if (range.From is { } from) q = q.Where(o => o.ExpectedCloseDate >= from);
        if (range.To is { } to) q = q.Where(o => o.ExpectedCloseDate <= to);

        var grouped = await q.GroupBy(o => o.Stage)
            .Select(g => new
            {
                Stage = g.Key,
                Count = g.Count(),
                Total = g.Sum(o => o.Amount),
                Weighted = g.Sum(o => o.Amount * o.Probability / 100m)
            }).ToListAsync(ct);

        var rows = Enum.GetValues<OpportunityStage>()
            .Select(stage =>
            {
                var g = grouped.FirstOrDefault(x => x.Stage == stage);
                return new PipelineStageRow(stage, g?.Count ?? 0, g?.Total ?? 0m, decimal.Round(g?.Weighted ?? 0m, 2));
            }).ToList();
        var open = rows.Where(r => r.Stage is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)).ToList();
        return new PipelineReport(currency, rows, open.Sum(r => r.Count), open.Sum(r => r.TotalAmount),
            open.Sum(r => r.WeightedAmount));
    }

    public async Task<SalesPerformanceReport> SalesPerformanceAsync(ReportRange range, CancellationToken ct)
    {
        var currency = range.Currency.ToUpperInvariant();
        var today = Today;
        var from = range.From ?? new DateOnly(today.Year, 1, 1);
        var to = range.To ?? today;

        var q = db.Opportunities.AsNoTracking()
            .Where(o => access.VisibleAccountIds().Contains(o.AccountId) && o.Currency == currency);
        if (range.OwnerId is { } owner) q = q.Where(o => o.OwnerId == owner);

        var rows = await q.GroupBy(o => o.OwnerId).Select(g => new
        {
            OwnerId = g.Key,
            WonCount = g.Count(o => o.Stage == OpportunityStage.ClosedWon && o.ActualCloseDate >= from && o.ActualCloseDate <= to),
            WonAmount = g.Where(o => o.Stage == OpportunityStage.ClosedWon && o.ActualCloseDate >= from && o.ActualCloseDate <= to)
                .Sum(o => (decimal?)o.Amount) ?? 0m,
            LostCount = g.Count(o => o.Stage == OpportunityStage.ClosedLost && o.ActualCloseDate >= from && o.ActualCloseDate <= to),
            OpenCount = g.Count(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost),
            OpenAmount = g.Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost)
                .Sum(o => (decimal?)o.Amount) ?? 0m
        }).ToListAsync(ct);

        var names = await db.Users.AsNoTracking().Where(u => rows.Select(r => r.OwnerId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var result = rows
            .Select(r => new SalesPerformanceRow(r.OwnerId, names.GetValueOrDefault(r.OwnerId, "Unknown"), r.WonCount,
                r.WonAmount, r.LostCount,
                r.WonCount + r.LostCount == 0 ? null : decimal.Round(100m * r.WonCount / (r.WonCount + r.LostCount), 1),
                r.OpenCount, r.OpenAmount))
            .OrderByDescending(r => r.WonAmount).ThenBy(r => r.OwnerName).ToList();
        return new SalesPerformanceReport(currency, from, to, result, result.Sum(r => r.WonCount), result.Sum(r => r.WonAmount));
    }

    public async Task<AccountsSummaryReport> AccountsSummaryAsync(CancellationToken ct)
    {
        var accounts = db.Accounts.AsNoTracking().Where(a => access.VisibleAccountIds().Contains(a.Id));
        var byStatus = await accounts.GroupBy(a => a.Status).Select(g => new CountBy<AccountStatus>(g.Key, g.Count())).ToListAsync(ct);
        var byTier = await accounts.GroupBy(a => a.Tier).Select(g => new CountBy<AccountTier>(g.Key, g.Count())).ToListAsync(ct);
        return new AccountsSummaryReport(byStatus.Sum(x => x.Count), byStatus.OrderBy(x => x.Key).ToList(),
            byTier.OrderBy(x => x.Key).ToList());
    }

    public async Task<ExpiringContractsReport> ExpiringContractsAsync(int days, CancellationToken ct)
    {
        var today = Today;
        var limit = today.AddDays(days);
        var rows = await db.Contracts.AsNoTracking()
            .Where(c => access.VisibleAccountIds().Contains(c.AccountId) && c.Status == ContractStatus.Active
                        && c.EndDate >= today && c.EndDate <= limit)
            .OrderBy(c => c.EndDate)
            .Select(c => new { c.Id, c.Number, c.Title, c.AccountId, c.Account.Company.Name, c.EndDate, c.Value, c.Currency, c.AutoRenew })
            .ToListAsync(ct);
        var list = rows.Select(c => new ExpiringContractRow(c.Id, $"CTR-{c.Number:D6}", c.Title, c.AccountId, c.Name,
            c.EndDate, c.EndDate.DayNumber - today.DayNumber, c.Value, c.Currency, c.AutoRenew)).ToList();
        return new ExpiringContractsReport(days, list.Count, list);
    }

    public async Task<TaskSummaryReport> TaskSummaryAsync(CancellationToken ct)
    {
        var today = Today;
        var tasks = db.Tasks.AsNoTracking().Where(t => access.VisibleAccountIds().Contains(t.AccountId));
        var byStatus = await tasks.GroupBy(t => t.Status).Select(g => new CountBy<WorkTaskStatus>(g.Key, g.Count())).ToListAsync(ct);
        var overdue = await tasks
            .Where(t => t.DueDate < today && t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled)
            .GroupBy(t => t.AssignedToId).Select(g => new { UserId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var names = await db.Users.AsNoTracking().Where(u => overdue.Select(o => o.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var rows = overdue.Select(o => new OverdueByAssignee(o.UserId, names.GetValueOrDefault(o.UserId, "Unknown"), o.Count))
            .OrderByDescending(o => o.OverdueCount).ThenBy(o => o.FullName).ToList();
        return new TaskSummaryReport(byStatus.OrderBy(x => x.Key).ToList(), rows.Sum(r => r.OverdueCount), rows);
    }

    public async Task<ActivitySummaryReport> ActivitySummaryAsync(ActivityRange range, CancellationToken ct)
    {
        var to = range.To ?? clock.GetUtcNow();
        var from = range.From ?? to.AddDays(-30);
        var q = db.Activities.AsNoTracking()
            .Where(a => access.VisibleAccountIds().Contains(a.AccountId) && a.OccurredAt >= from && a.OccurredAt <= to);
        var byType = await q.GroupBy(a => a.Type).Select(g => new CountBy<ActivityType>(g.Key, g.Count())).ToListAsync(ct);
        var byUserIds = await q.GroupBy(a => a.PerformedById).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);
        var names = await db.Users.AsNoTracking().Where(u => byUserIds.Select(x => x.Id).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var byUser = byUserIds.Select(x => new CountBy<string>(names.GetValueOrDefault(x.Id, "Unknown"), x.Count))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Key).ToList();
        return new ActivitySummaryReport(from, to, byType.Sum(x => x.Count), byType.OrderBy(x => x.Key).ToList(), byUser);
    }
}

public sealed class ReportRangeValidator : AbstractValidator<ReportRange>
{
    public ReportRangeValidator()
    {
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.From <= x.To)
            .WithMessage("From must not be after To.");
    }
}

public sealed class ActivityRangeValidator : AbstractValidator<ActivityRange>
{
    public ActivityRangeValidator() =>
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.From <= x.To).WithMessage("From must not be after To.");
}
