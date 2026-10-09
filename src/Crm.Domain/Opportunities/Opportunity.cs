using Crm.Domain.Accounts;
using Crm.Domain.Common;

namespace Crm.Domain.Opportunities;

public enum OpportunityStage
{
    Prospecting = 0,
    Qualification = 1,
    Proposal = 2,
    Negotiation = 3,
    ClosedWon = 4,
    ClosedLost = 5
}

public class Opportunity : Entity
{
    private Opportunity() { }

    public Opportunity(Guid accountId, Guid ownerId, string name, string? description, decimal amount,
        string currency, DateOnly expectedCloseDate, Guid? primaryContactId)
    {
        AccountId = accountId;
        OwnerId = ownerId;
        PrimaryContactId = primaryContactId;
        Stage = OpportunityStage.Prospecting;
        Probability = DefaultProbability(Stage);
        Apply(name, description, amount, currency, expectedCloseDate);
    }

    public Guid AccountId { get; private set; }
    public Account Account { get; private set; } = null!;
    public Guid OwnerId { get; private set; }
    public Guid? PrimaryContactId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public OpportunityStage Stage { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    /// <summary>Win probability in percent (0-100).</summary>
    public int Probability { get; private set; }
    public DateOnly ExpectedCloseDate { get; private set; }
    public DateOnly? ActualCloseDate { get; private set; }
    public string? LossReason { get; private set; }

    public bool IsOpen => Stage is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost);
    public decimal WeightedAmount => decimal.Round(Amount * Probability / 100m, 2);

    public static int DefaultProbability(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Prospecting => 10,
        OpportunityStage.Qualification => 25,
        OpportunityStage.Proposal => 50,
        OpportunityStage.Negotiation => 75,
        OpportunityStage.ClosedWon => 100,
        _ => 0
    };

    public void Update(string name, string? description, decimal amount, string currency,
        DateOnly expectedCloseDate, Guid? primaryContactId)
    {
        EnsureOpen();
        Apply(name, description, amount, currency, expectedCloseDate);
        PrimaryContactId = primaryContactId;
    }

    public void Reassign(Guid newOwnerId)
    {
        EnsureOpen();
        OwnerId = newOwnerId;
    }

    /// <summary>
    /// Pipeline rules: open stages can move forward or back; a deal can only be won from Negotiation;
    /// it can be lost from any open stage but a reason is mandatory; closed deals are immutable.
    /// </summary>
    public void ChangeStage(OpportunityStage target, string? lossReason, DateOnly today)
    {
        EnsureOpen();
        if (target == Stage) return;

        switch (target)
        {
            case OpportunityStage.ClosedWon:
                if (Stage != OpportunityStage.Negotiation)
                    throw new DomainException("An opportunity can only be won from the Negotiation stage.", "invalid_stage_transition");
                ActualCloseDate = today;
                break;
            case OpportunityStage.ClosedLost:
                LossReason = Guard.Required(lossReason, "Loss reason", 500);
                ActualCloseDate = today;
                break;
            default:
                if (Math.Abs((int)target - (int)Stage) > 1)
                    throw new DomainException(
                        $"Cannot move from {Stage} to {target}; stages must be traversed one at a time.",
                        "invalid_stage_transition");
                break;
        }

        Stage = target;
        Probability = DefaultProbability(target);
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
            throw new DomainException("Closed opportunities cannot be modified.", "opportunity_closed");
    }

    private void Apply(string name, string? description, decimal amount, string currency, DateOnly expectedCloseDate)
    {
        Name = Guard.Required(name, "Name", 200);
        Description = Guard.Optional(description, "Description", 4000);
        Amount = Guard.PositiveMoney(amount, "Amount");
        Currency = Guard.Currency(currency);
        ExpectedCloseDate = expectedCloseDate;
    }
}
