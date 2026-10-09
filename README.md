# B2B CRM & Account Management System

> **STATUS: INCOMPLETE AND UNTESTED.**
> This is a partially written work in progress. **Nothing has been compiled, run or tested yet.**
> It was committed only to move the work to another machine. Expect compile errors.

## Target

Enterprise B2B CRM on .NET 10 (LTS), ASP.NET Core, EF Core, PostgreSQL, REST + OpenAPI, Docker, xUnit, GitHub Actions.
Modular layers: Domain, Application, Infrastructure, Api.

## What exists

- Solution scaffold: `src/Crm.Domain`, `Crm.Application`, `Crm.Infrastructure`, `Crm.Api`, `tests/Crm.UnitTests`, `tests/Crm.IntegrationTests`. NuGet packages are added to the csproj files.
- **Domain (written; built once successfully):** entities (User, Company, Contact, Account + team, Opportunity, Contract, Activity, WorkTask, AuditLog), business rules, permission matrix per role, domain services (account lifecycle, account access policy, company hierarchy).
- **Application (written; not fully compiled):** services, DTOs and FluentValidation validators for users, companies, contacts, accounts, opportunities, contracts, tasks, activities, reports, audit; contract expiry/renewal job logic; DI registration.

## What is missing

- Infrastructure: EF Core DbContext/configurations, migrations, audit SaveChanges interceptor, JWT issuing, password hashing, transaction runner, Postgres advisory job lock, background worker, seeding.
- Api: controllers, auth/authorization policies, exception-to-ProblemDetails handling, OpenAPI/Scalar, Serilog, Program.cs (currently the template).
- All unit and integration tests.
- Dockerfile, docker-compose, GitHub Actions workflow.
- Documentation: architecture, database diagram, API docs, technical decisions, setup guide.

## Notes

- `tools/dn.sh` runs the .NET SDK via Docker (used on a Mac without a local SDK). On Windows install the .NET 10 SDK and use `dotnet` directly.
- Known risks to verify when first building: EF translation of `DateOnly.AddDays` with a column argument in `ContractLifecycleService`, `PadLeft` string projections, and `object`-typed sort key selectors in `Paging.cs`.
