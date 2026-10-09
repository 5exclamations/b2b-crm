namespace Crm.Application.Common;

/// <summary>Common paging/sorting query parameters. Sort uses "field" (ascending) or "-field" (descending).</summary>
public record PageRequest
{
    public const int MaxPageSize = 200;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Sort { get; init; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public static class QueryableExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PageRequest request,
        CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, PageRequest.MaxPageSize);
        var total = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query, ct);
        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            query.Skip((page - 1) * size).Take(size), ct);
        return new PagedResult<T>(items, page, size, total);
    }

    /// <summary>
    /// Applies a whitelisted sort. <paramref name="selectors"/> maps public sort keys to key selectors; an unknown key
    /// is rejected by validation, so here it falls back to the default order.
    /// </summary>
    public static IOrderedQueryable<T> ApplySort<T>(this IQueryable<T> query, string? sort,
        IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<T, object?>>> selectors,
        System.Linq.Expressions.Expression<Func<T, object?>> defaultKey, bool defaultDescending = false)
    {
        var descending = sort?.StartsWith('-') == true;
        var key = sort?.TrimStart('-').ToLowerInvariant();
        if (key is not null && selectors.TryGetValue(key, out var selector))
            return descending ? query.OrderByDescending(selector) : query.OrderBy(selector);
        return defaultDescending ? query.OrderByDescending(defaultKey) : query.OrderBy(defaultKey);
    }
}

public static class SortValidation
{
    public static bool IsAllowed(string? sort, IEnumerable<string> allowed) =>
        string.IsNullOrEmpty(sort) || allowed.Contains(sort.TrimStart('-'), StringComparer.OrdinalIgnoreCase);
}
