using Crm.Domain.Users;

namespace Crm.Domain;

/// <summary>Fine-grained permissions granted to roles. Endpoints are protected with these, not with raw roles.</summary>
public static class Permissions
{
    public const string CompaniesRead = "companies.read";
    public const string CompaniesWrite = "companies.write";
    public const string AccountsRead = "accounts.read";
    public const string AccountsWrite = "accounts.write";
    public const string AccountsReassign = "accounts.reassign";
    public const string OpportunitiesRead = "opportunities.read";
    public const string OpportunitiesWrite = "opportunities.write";
    public const string ContractsRead = "contracts.read";
    public const string ContractsWrite = "contracts.write";
    public const string ContractsApprove = "contracts.approve";
    public const string ActivitiesRead = "activities.read";
    public const string ActivitiesWrite = "activities.write";
    public const string ReportsRead = "reports.read";
    public const string AuditRead = "audit.read";
    public const string UsersManage = "users.manage";
    public const string JobsRun = "jobs.run";

    public static IReadOnlyCollection<string> All { get; } =
    [
        CompaniesRead, CompaniesWrite, AccountsRead, AccountsWrite, AccountsReassign, OpportunitiesRead,
        OpportunitiesWrite, ContractsRead, ContractsWrite, ContractsApprove, ActivitiesRead, ActivitiesWrite,
        ReportsRead, AuditRead, UsersManage, JobsRun
    ];

    private static readonly string[] ReadOnlySet =
        [CompaniesRead, AccountsRead, OpportunitiesRead, ContractsRead, ActivitiesRead, ReportsRead];

    private static readonly string[] RepSet =
    [
        .. ReadOnlySet, CompaniesWrite, AccountsWrite, OpportunitiesWrite, ContractsWrite, ActivitiesWrite
    ];

    private static readonly string[] ManagerSet = [.. RepSet, AccountsReassign, ContractsApprove, AuditRead];

    public static IReadOnlyCollection<string> For(UserRole role) => role switch
    {
        UserRole.Admin => All,
        UserRole.SalesManager => ManagerSet,
        UserRole.SalesRep => RepSet,
        _ => ReadOnlySet
    };

    public static bool Has(UserRole role, string permission) => For(role).Contains(permission);
}
