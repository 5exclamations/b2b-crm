namespace Crm.Application.Common;

public sealed record UserSummaryDto(Guid Id, string FullName);
public sealed record CompanySummaryDto(Guid Id, string Name);
public sealed record AccountSummaryDto(Guid Id, string AccountNumber, string CompanyName);
