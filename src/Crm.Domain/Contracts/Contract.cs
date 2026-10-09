using Crm.Domain.Accounts;
using Crm.Domain.Common;

namespace Crm.Domain.Contracts;

public enum ContractStatus { Draft, Active, Expired, Terminated }

public class Contract : Entity
{
    public const int DefaultRenewalNoticeDays = 60;

    private Contract() { }

    public Contract(Guid accountId, Guid? opportunityId, string title, DateOnly startDate, DateOnly endDate,
        decimal value, string currency, bool autoRenew, int renewalNoticeDays)
    {
        AccountId = accountId;
        OpportunityId = opportunityId;
        Status = ContractStatus.Draft;
        Apply(title, startDate, endDate, value, currency, autoRenew, renewalNoticeDays);
    }

    public int Number { get; private set; }
    public string ContractNumber => $"CTR-{Number:D6}";
    public Guid AccountId { get; private set; }
    public Account Account { get; private set; } = null!;
    public Guid? OpportunityId { get; private set; }
    public string Title { get; private set; } = null!;
    public ContractStatus Status { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public decimal Value { get; private set; }
    public string Currency { get; private set; } = null!;
    public bool AutoRenew { get; private set; }
    public int RenewalNoticeDays { get; private set; }
    public int RenewalCount { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? RenewalReminderSentAt { get; private set; }
    public string? TerminationReason { get; private set; }

    public DateOnly RenewalNoticeDate => EndDate.AddDays(-RenewalNoticeDays);

    public void Update(string title, DateOnly startDate, DateOnly endDate, decimal value, string currency,
        bool autoRenew, int renewalNoticeDays)
    {
        if (Status != ContractStatus.Draft)
            throw new DomainException("Only draft contracts can be edited.", "contract_not_editable");
        Apply(title, startDate, endDate, value, currency, autoRenew, renewalNoticeDays);
    }

    public void Activate(DateTimeOffset now, DateOnly today)
    {
        if (Status != ContractStatus.Draft)
            throw new DomainException("Only draft contracts can be activated.", "invalid_contract_transition");
        if (EndDate <= today)
            throw new DomainException("A contract that has already ended cannot be activated.", "contract_already_ended");
        Status = ContractStatus.Active;
        ActivatedAt = now;
    }

    public void Terminate(string reason)
    {
        if (Status != ContractStatus.Active)
            throw new DomainException("Only active contracts can be terminated.", "invalid_contract_transition");
        TerminationReason = Guard.Required(reason, "Termination reason", 1000);
        Status = ContractStatus.Terminated;
    }

    /// <summary>Called by the lifecycle job once the end date has passed.</summary>
    public void Expire()
    {
        if (Status != ContractStatus.Active)
            throw new DomainException("Only active contracts can expire.", "invalid_contract_transition");
        Status = ContractStatus.Expired;
    }

    /// <summary>Extends the contract by its original term, keeping it active.</summary>
    public void Renew()
    {
        if (Status != ContractStatus.Active)
            throw new DomainException("Only active contracts can be renewed.", "invalid_contract_transition");
        var term = EndDate.DayNumber - StartDate.DayNumber + 1;
        StartDate = EndDate.AddDays(1);
        EndDate = StartDate.AddDays(term - 1);
        RenewalCount++;
        RenewalReminderSentAt = null;
    }

    public void MarkRenewalReminderSent(DateTimeOffset at) => RenewalReminderSentAt = at;

    private void Apply(string title, DateOnly startDate, DateOnly endDate, decimal value, string currency,
        bool autoRenew, int renewalNoticeDays)
    {
        if (endDate <= startDate)
            throw new DomainException("End date must be after the start date.", "invalid_period");
        if (renewalNoticeDays is < 0 or > 365)
            throw new DomainException("Renewal notice must be between 0 and 365 days.", "invalid_value");
        Title = Guard.Required(title, "Title", 200);
        StartDate = startDate;
        EndDate = endDate;
        Value = Guard.PositiveMoney(value, "Value");
        Currency = Guard.Currency(currency);
        AutoRenew = autoRenew;
        RenewalNoticeDays = renewalNoticeDays;
    }
}
