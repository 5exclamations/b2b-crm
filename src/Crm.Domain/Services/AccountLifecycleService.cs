using Crm.Domain.Accounts;
using Crm.Domain.Common;

namespace Crm.Domain.Services;

/// <summary>Facts about an account required to evaluate status transitions.</summary>
public readonly record struct AccountActivity(int ActiveContracts, int OpenOpportunities);

/// <summary>
/// Domain service owning account status transitions. It needs facts from other aggregates
/// (contracts, opportunities) which the application layer supplies, keeping the domain persistence-free.
/// </summary>
public static class AccountLifecycleService
{
    public static void ChangeStatus(Account account, AccountStatus target, AccountActivity activity, bool actorIsManager)
    {
        if (account.Status == target) return;

        switch (target)
        {
            case AccountStatus.Prospect:
                throw new DomainException("An account cannot return to Prospect.", "invalid_account_transition");

            case AccountStatus.Active:
                if (account.Status == AccountStatus.Churned && !actorIsManager)
                    throw new DomainException("Only managers can reactivate a churned account.", "invalid_account_transition");
                break;

            case AccountStatus.OnHold:
                if (account.Status == AccountStatus.Churned)
                    throw new DomainException("A churned account cannot be put on hold.", "invalid_account_transition");
                break;

            case AccountStatus.Churned:
                if (activity.ActiveContracts > 0)
                    throw new DomainException(
                        $"Account has {activity.ActiveContracts} active contract(s); terminate them before churning.",
                        "account_has_active_contracts");
                if (activity.OpenOpportunities > 0)
                    throw new DomainException(
                        $"Account has {activity.OpenOpportunities} open opportunit(ies); close them before churning.",
                        "account_has_open_opportunities");
                break;
        }

        account.SetStatus(target);
    }
}
