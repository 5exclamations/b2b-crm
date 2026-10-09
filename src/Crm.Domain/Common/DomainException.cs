namespace Crm.Domain.Common;

/// <summary>Raised when a business rule is violated. Mapped to HTTP 422 by the API.</summary>
public class DomainException(string message, string code = "business_rule_violation") : Exception(message)
{
    public string Code { get; } = code;
}
