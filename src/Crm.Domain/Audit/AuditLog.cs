namespace Crm.Domain.Audit;

public enum AuditAction { Created, Updated, Deleted }

/// <summary>Immutable record of a data change. Written by the persistence layer in the same transaction as the change.</summary>
public class AuditLog
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset Timestamp { get; init; }
    public Guid? UserId { get; init; }
    public string? UserEmail { get; init; }
    public string EntityType { get; init; } = null!;
    public string EntityId { get; init; } = null!;
    public AuditAction Action { get; init; }
    /// <summary>JSON object: { "Field": { "old": ..., "new": ... } }.</summary>
    public string Changes { get; init; } = "{}";
    public string? CorrelationId { get; init; }
}
