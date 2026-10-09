namespace Crm.Domain.Common;

/// <summary>Base type for persisted entities. Audit columns are stamped by the persistence layer.</summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}
