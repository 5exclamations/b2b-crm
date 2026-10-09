using Crm.Application.Accounts;
using Crm.Application.Activities;
using Crm.Application.Audit;
using Crm.Application.Common;
using Crm.Application.Companies;
using Crm.Application.Contracts;
using Crm.Application.Opportunities;
using Crm.Application.Reports;
using Crm.Application.Users;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<CompanyRequestValidator>(includeInternalTypes: true);

        services.AddScoped<IAccountAccess, AccountAccess>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<IContactService, ContactService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IOpportunityService, OpportunityService>();
        services.AddScoped<IContractService, ContractService>();
        services.AddScoped<IContractLifecycleService, ContractLifecycleService>();
        services.AddScoped<IActivityService, ActivityService>();
        services.AddScoped<ITaskService, TaskService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IAuditService, AuditService>();
        return services;
    }
}
