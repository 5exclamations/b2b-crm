using Crm.Application.Common;
using Crm.Domain.Activities;
using Crm.Domain.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Crm.Application.Contracts;

public sealed record ContractLifecycleResult(int Expired, int Renewed, int RemindersCreated, bool Skipped = false);

public interface IContractLifecycleService
{
    /// <summary>
    /// Time-driven contract maintenance: expires or auto-renews contracts past their end date and creates
    /// renewal reminder tasks. Idempotent and safe to run concurrently on several instances.
    /// </summary>
    Task<ContractLifecycleResult> RunAsync(CancellationToken ct);
}

internal sealed class ContractLifecycleService(ICrmDbContext db, ITransactionRunner tx, IJobLock jobLock,
    TimeProvider clock, ILogger<ContractLifecycleService> logger) : IContractLifecycleService
{
    private const int BatchSize = 500;

    public async Task<ContractLifecycleResult> RunAsync(CancellationToken ct)
    {
        await using var handle = await jobLock.TryAcquireAsync("contract-lifecycle", ct);
        if (handle is null)
        {
            logger.LogInformation("Contract lifecycle job skipped: another instance holds the lock");
            return new ContractLifecycleResult(0, 0, 0, Skipped: true);
        }

        var result = await tx.ExecuteAsync(async token =>
        {
            var now = clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            int expired = 0, renewed = 0, reminders = 0;

            var ended = await db.Contracts
                .Where(c => c.Status == ContractStatus.Active && c.EndDate < today)
                .OrderBy(c => c.EndDate).Take(BatchSize).ToListAsync(token);
            foreach (var contract in ended)
            {
                if (contract.AutoRenew)
                {
                    // A job outage may span several terms; catch up so the contract ends up covering today.
                    while (contract.EndDate < today) contract.Renew();
                    renewed++;
                    logger.LogInformation("Contract {ContractNumber} auto-renewed to {EndDate} (renewal #{Count})",
                        contract.ContractNumber, contract.EndDate, contract.RenewalCount);
                }
                else
                {
                    contract.Expire();
                    expired++;
                    logger.LogInformation("Contract {ContractNumber} expired on {EndDate}",
                        contract.ContractNumber, contract.EndDate);
                }
            }

            var dueForReminder = await db.Contracts
                .Include(c => c.Account)
                .Where(c => c.Status == ContractStatus.Active && c.RenewalReminderSentAt == null
                            && c.EndDate >= today && c.EndDate.AddDays(-c.RenewalNoticeDays) <= today)
                .OrderBy(c => c.EndDate).Take(BatchSize).ToListAsync(token);
            foreach (var contract in dueForReminder)
            {
                var title = contract.AutoRenew
                    ? $"Contract {contract.ContractNumber} auto-renews on {contract.EndDate.AddDays(1):yyyy-MM-dd}"
                    : $"Renew contract {contract.ContractNumber} before {contract.EndDate:yyyy-MM-dd}";
                db.Tasks.Add(new WorkTask(contract.AccountId, contract.Account.OwnerId, title,
                    $"Contract '{contract.Title}' ends on {contract.EndDate:yyyy-MM-dd}. Review renewal terms with the customer.",
                    contract.EndDate, TaskPriority.High, contract.OpportunityId, today));
                contract.MarkRenewalReminderSent(now);
                reminders++;
            }

            await db.SaveChangesAsync(token);
            return new ContractLifecycleResult(expired, renewed, reminders);
        }, ct);

        logger.LogInformation("Contract lifecycle run finished: {Expired} expired, {Renewed} renewed, {Reminders} reminders",
            result.Expired, result.Renewed, result.RemindersCreated);
        return result;
    }
}
