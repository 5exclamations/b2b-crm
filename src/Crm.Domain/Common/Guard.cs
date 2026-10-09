namespace Crm.Domain.Common;

internal static class Guard
{
    public static string Required(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new DomainException($"{field} is required.", "required");
        if (trimmed.Length > maxLength)
            throw new DomainException($"{field} must not exceed {maxLength} characters.", "too_long");
        return trimmed;
    }

    public static string? Optional(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > maxLength)
            throw new DomainException($"{field} must not exceed {maxLength} characters.", "too_long");
        return trimmed;
    }

    public static decimal PositiveMoney(decimal value, string field)
    {
        if (value <= 0) throw new DomainException($"{field} must be greater than zero.", "invalid_amount");
        return decimal.Round(value, 2);
    }

    public static string Currency(string? value)
    {
        var c = value?.Trim().ToUpperInvariant();
        if (c is not { Length: 3 } || !c.All(char.IsAsciiLetterUpper))
            throw new DomainException("Currency must be a 3-letter ISO 4217 code.", "invalid_currency");
        return c;
    }
}
