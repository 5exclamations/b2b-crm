using FluentValidation;

namespace Crm.Application.Common;

/// <summary>Reusable rules for <see cref="PageRequest"/> descendants.</summary>
public sealed class PageRequestValidator<T> : AbstractValidator<T> where T : PageRequest
{
    public PageRequestValidator(IEnumerable<string> allowedSortKeys)
    {
        var allowed = allowedSortKeys.ToArray();
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize);
        RuleFor(x => x.Sort).Must(s => SortValidation.IsAllowed(s, allowed))
            .WithMessage($"Sort must be one of: {string.Join(", ", allowed)} (prefix with '-' for descending).");
    }
}
