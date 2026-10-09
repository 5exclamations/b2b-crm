using Crm.Domain.Accounts;
using Crm.Domain.Common;

namespace Crm.Domain.Activities;

public enum ActivityType { Call, Meeting, Email, Note }

/// <summary>A logged interaction with a customer (call, meeting, email, note).</summary>
public class Activity : Entity
{
    private Activity() { }

    public Activity(Guid accountId, Guid performedById, ActivityType type, string subject, string? notes,
        DateTimeOffset occurredAt, int? durationMinutes, Guid? contactId, Guid? opportunityId, DateTimeOffset now)
    {
        if (occurredAt > now.AddMinutes(5))
            throw new DomainException("Activities cannot be logged in the future; create a task instead.", "invalid_date");
        if (durationMinutes is < 0 or > 24 * 60)
            throw new DomainException("Duration must be between 0 and 1440 minutes.", "invalid_value");
        AccountId = accountId;
        PerformedById = performedById;
        Type = type;
        Subject = Guard.Required(subject, "Subject", 200);
        Notes = Guard.Optional(notes, "Notes", 4000);
        OccurredAt = occurredAt;
        DurationMinutes = durationMinutes;
        ContactId = contactId;
        OpportunityId = opportunityId;
    }

    public Guid AccountId { get; private set; }
    public Account Account { get; private set; } = null!;
    public Guid PerformedById { get; private set; }
    public Guid? ContactId { get; private set; }
    public Guid? OpportunityId { get; private set; }
    public ActivityType Type { get; private set; }
    public string Subject { get; private set; } = null!;
    public string? Notes { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public int? DurationMinutes { get; private set; }
}
