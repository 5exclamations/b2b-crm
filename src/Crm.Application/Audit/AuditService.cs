using Crm.Application.Common;
using Crm.Domain.Audit;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Audit;

public sealed record AuditLogDto(Guid Id, DateTimeOffset Timestamp, Guid? UserId, string? UserEmail, string EntityType,
    string EntityId, AuditAction Action, string Changes, string? CorrelationId);

public sealed record AuditQuery : PageRequest
{
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public Guid? UserId { get; init; }
    public AuditAction? Action { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

public interface IAuditService
{
    Task<PagedResult<AuditLogDto>> QueryAsync(AuditQuery query, CancellationToken ct);
}

internal sealed class AuditService(ICrmDbContext db) : IAuditService
{
    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditQuery query, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.EntityType)) q = q.Where(a => a.EntityType == query.EntityType);
        if (!string.IsNullOrWhiteSpace(query.EntityId)) q = q.Where(a => a.EntityId == query.EntityId);
        if (query.UserId is { } u) q = q.Where(a => a.UserId == u);
        if (query.Action is { } act) q = q.Where(a => a.Action == act);
        if (query.From is { } from) q = q.Where(a => a.Timestamp >= from);
        if (query.To is { } to) q = q.Where(a => a.Timestamp <= to);

        return await q.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Select(a => new AuditLogDto(a.Id, a.Timestamp, a.UserId, a.UserEmail, a.EntityType, a.EntityId, a.Action,
                a.Changes, a.CorrelationId))
            .ToPagedAsync(query, ct);
    }
}

public sealed class AuditQueryValidator : AbstractValidator<AuditQuery>
{
    public AuditQueryValidator()
    {
        Include(new PageRequestValidator<AuditQuery>([]));
        RuleFor(x => x.Action).IsInEnum().When(x => x.Action.HasValue);
        RuleFor(x => x.EntityType).MaximumLength(100);
    }
}
