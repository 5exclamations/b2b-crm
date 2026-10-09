using Crm.Application.Common;
using Crm.Domain.Activities;
using Crm.Domain.Common;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Activities;

public sealed record TaskDto(Guid Id, AccountSummaryDto Account, Guid? OpportunityId, UserSummaryDto AssignedTo,
    string Title, string? Description, DateOnly DueDate, TaskPriority Priority, WorkTaskStatus Status,
    bool IsOverdue, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt);

public sealed record CreateTaskRequest(Guid AccountId, Guid? AssignedToId, string Title, string? Description,
    DateOnly DueDate, TaskPriority Priority, Guid? OpportunityId);
public sealed record UpdateTaskRequest(string Title, string? Description, DateOnly DueDate, TaskPriority Priority);
public sealed record AssignTaskRequest(Guid AssigneeId);

public sealed record TaskQuery : PageRequest
{
    public string? Search { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? AssignedToId { get; init; }
    public WorkTaskStatus? Status { get; init; }
    public TaskPriority? Priority { get; init; }
    public DateOnly? DueFrom { get; init; }
    public DateOnly? DueTo { get; init; }
    public bool? Overdue { get; init; }
    /// <summary>When true only tasks assigned to the caller.</summary>
    public bool? Mine { get; init; }
}

public interface ITaskService
{
    Task<PagedResult<TaskDto>> ListAsync(TaskQuery query, CancellationToken ct);
    Task<TaskDto> GetAsync(Guid id, CancellationToken ct);
    Task<TaskDto> CreateAsync(CreateTaskRequest request, CancellationToken ct);
    Task<TaskDto> UpdateAsync(Guid id, UpdateTaskRequest request, CancellationToken ct);
    Task<TaskDto> StartAsync(Guid id, CancellationToken ct);
    Task<TaskDto> CompleteAsync(Guid id, CancellationToken ct);
    Task<TaskDto> CancelAsync(Guid id, CancellationToken ct);
    Task<TaskDto> AssignAsync(Guid id, AssignTaskRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

internal sealed class TaskService(ICrmDbContext db, IAccountAccess access, ICurrentUser user, TimeProvider clock)
    : ITaskService
{
    internal static readonly IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<WorkTask, object?>>> Sorts =
        new Dictionary<string, System.Linq.Expressions.Expression<Func<WorkTask, object?>>>
        {
            ["due"] = t => t.DueDate,
            ["priority"] = t => t.Priority,
            ["status"] = t => t.Status,
            ["title"] = t => t.Title,
            ["created"] = t => t.CreatedAt
        };

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private IQueryable<TaskDto> Project(IQueryable<WorkTask> q)
    {
        var today = Today;
        return q.Select(t => new TaskDto(
            t.Id,
            new AccountSummaryDto(t.AccountId, "ACC-" + t.Account.Number.ToString().PadLeft(6, '0'), t.Account.Company.Name),
            t.OpportunityId,
            new UserSummaryDto(t.AssignedToId, db.Users.Where(u => u.Id == t.AssignedToId).Select(u => u.FullName).FirstOrDefault()!),
            t.Title, t.Description, t.DueDate, t.Priority, t.Status,
            t.DueDate < today && t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled,
            t.CompletedAt, t.CreatedAt));
    }

    public async Task<PagedResult<TaskDto>> ListAsync(TaskQuery query, CancellationToken ct)
    {
        var today = Today;
        var q = db.Tasks.AsNoTracking().Where(t => access.VisibleAccountIds().Contains(t.AccountId));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            q = q.Where(t => t.Title.ToLower().Contains(term) || (t.Description != null && t.Description.ToLower().Contains(term)));
        }
        if (query.AccountId is { } a) q = q.Where(t => t.AccountId == a);
        if (query.AssignedToId is { } u) q = q.Where(t => t.AssignedToId == u);
        if (query.Status is { } s) q = q.Where(t => t.Status == s);
        if (query.Priority is { } p) q = q.Where(t => t.Priority == p);
        if (query.DueFrom is { } from) q = q.Where(t => t.DueDate >= from);
        if (query.DueTo is { } to) q = q.Where(t => t.DueDate <= to);
        if (query.Mine == true)
        {
            var me = user.RequireId();
            q = q.Where(t => t.AssignedToId == me);
        }
        if (query.Overdue is { } overdue)
        {
            q = overdue
                ? q.Where(t => t.DueDate < today && t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled)
                : q.Where(t => !(t.DueDate < today && t.Status != WorkTaskStatus.Completed && t.Status != WorkTaskStatus.Cancelled));
        }
        return await Project(q.ApplySort(query.Sort, Sorts, t => t.DueDate)).ToPagedAsync(query, ct);
    }

    public async Task<TaskDto> GetAsync(Guid id, CancellationToken ct) =>
        await Project(db.Tasks.AsNoTracking()
            .Where(t => t.Id == id && access.VisibleAccountIds().Contains(t.AccountId))).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Task", id);

    public async Task<TaskDto> CreateAsync(CreateTaskRequest r, CancellationToken ct)
    {
        var account = await access.GetWritableAsync(r.AccountId, ct);
        var assigneeId = r.AssignedToId ?? user.RequireId();
        await EnsureAssigneeAsync(assigneeId, ct);
        if (r.OpportunityId is { } oid && !await db.Opportunities.AnyAsync(o => o.Id == oid && o.AccountId == account.Id, ct))
            throw new DomainException("The opportunity does not belong to this account.", "invalid_opportunity");

        var task = new WorkTask(account.Id, assigneeId, r.Title, r.Description, r.DueDate, r.Priority,
            r.OpportunityId, Today);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        return await GetAsync(task.Id, ct);
    }

    public Task<TaskDto> UpdateAsync(Guid id, UpdateTaskRequest r, CancellationToken ct) =>
        MutateAsync(id, t => t.Update(r.Title, r.Description, r.DueDate, r.Priority), ct);

    public Task<TaskDto> StartAsync(Guid id, CancellationToken ct) => MutateAsync(id, t => t.Start(), ct);

    public Task<TaskDto> CompleteAsync(Guid id, CancellationToken ct) =>
        MutateAsync(id, t => t.Complete(clock.GetUtcNow()), ct);

    public Task<TaskDto> CancelAsync(Guid id, CancellationToken ct) => MutateAsync(id, t => t.Cancel(), ct);

    public async Task<TaskDto> AssignAsync(Guid id, AssignTaskRequest r, CancellationToken ct)
    {
        await EnsureAssigneeAsync(r.AssigneeId, ct);
        return await MutateAsync(id, t => t.Reassign(r.AssigneeId), ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var task = await LoadWritableAsync(id, ct);
        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);
    }

    private async Task<TaskDto> MutateAsync(Guid id, Action<WorkTask> change, CancellationToken ct)
    {
        var task = await LoadWritableAsync(id, ct);
        change(task);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<WorkTask> LoadWritableAsync(Guid id, CancellationToken ct)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Task", id);
        await access.GetWritableAsync(task.AccountId, ct);
        return task;
    }

    private async Task EnsureAssigneeAsync(Guid assigneeId, CancellationToken ct)
    {
        var assignee = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == assigneeId, ct)
                       ?? throw new NotFoundException("User", assigneeId);
        if (!assignee.CanOwnRecords)
            throw new DomainException("Tasks can only be assigned to active users with a sales role.", "invalid_assignee");
    }
}

public sealed class CreateTaskRequestValidator : AbstractValidator<CreateTaskRequest>
{
    public CreateTaskRequestValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(4000);
        RuleFor(x => x.Priority).IsInEnum();
    }
}

public sealed class UpdateTaskRequestValidator : AbstractValidator<UpdateTaskRequest>
{
    public UpdateTaskRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(4000);
        RuleFor(x => x.Priority).IsInEnum();
    }
}

public sealed class AssignTaskRequestValidator : AbstractValidator<AssignTaskRequest>
{
    public AssignTaskRequestValidator() => RuleFor(x => x.AssigneeId).NotEmpty();
}

public sealed class TaskQueryValidator : AbstractValidator<TaskQuery>
{
    public TaskQueryValidator()
    {
        Include(new PageRequestValidator<TaskQuery>(TaskService.Sorts.Keys));
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.Priority).IsInEnum().When(x => x.Priority.HasValue);
    }
}
