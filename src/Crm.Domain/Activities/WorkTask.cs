using Crm.Domain.Accounts;
using Crm.Domain.Common;

namespace Crm.Domain.Activities;

public enum WorkTaskStatus { Open, InProgress, Completed, Cancelled }
public enum TaskPriority { Low, Normal, High, Urgent }

public class WorkTask : Entity
{
    private WorkTask() { }

    public WorkTask(Guid accountId, Guid assignedToId, string title, string? description, DateOnly dueDate,
        TaskPriority priority, Guid? opportunityId, DateOnly today)
    {
        if (dueDate < today)
            throw new DomainException("Due date cannot be in the past.", "invalid_date");
        AccountId = accountId;
        AssignedToId = assignedToId;
        OpportunityId = opportunityId;
        DueDate = dueDate;
        Priority = priority;
        Title = Guard.Required(title, "Title", 200);
        Description = Guard.Optional(description, "Description", 4000);
        Status = WorkTaskStatus.Open;
    }

    public Guid AccountId { get; private set; }
    public Account Account { get; private set; } = null!;
    public Guid? OpportunityId { get; private set; }
    public Guid AssignedToId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public DateOnly DueDate { get; private set; }
    public TaskPriority Priority { get; private set; }
    public WorkTaskStatus Status { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsClosed => Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled;
    public bool IsOverdue(DateOnly today) => !IsClosed && DueDate < today;

    public void Update(string title, string? description, DateOnly dueDate, TaskPriority priority)
    {
        EnsureNotClosed();
        Title = Guard.Required(title, "Title", 200);
        Description = Guard.Optional(description, "Description", 4000);
        DueDate = dueDate;
        Priority = priority;
    }

    public void Reassign(Guid assigneeId)
    {
        EnsureNotClosed();
        AssignedToId = assigneeId;
    }

    public void Start()
    {
        EnsureNotClosed();
        Status = WorkTaskStatus.InProgress;
    }

    public void Complete(DateTimeOffset now)
    {
        EnsureNotClosed();
        Status = WorkTaskStatus.Completed;
        CompletedAt = now;
    }

    public void Cancel()
    {
        EnsureNotClosed();
        Status = WorkTaskStatus.Cancelled;
    }

    private void EnsureNotClosed()
    {
        if (IsClosed) throw new DomainException($"A {Status.ToString().ToLowerInvariant()} task cannot be changed.", "task_closed");
    }
}
