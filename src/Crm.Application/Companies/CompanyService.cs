using Crm.Application.Common;
using Crm.Domain.Companies;
using Crm.Domain.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Companies;

public sealed record CompanyDto(Guid Id, string Name, string? LegalName, string? TaxId, string? Industry,
    string? Website, string? Country, string? City, int? EmployeeCount, Guid? ParentCompanyId,
    string? ParentCompanyName, int ContactCount, Guid? AccountId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CompanyRequest(string Name, string? LegalName, string? TaxId, string? Industry, string? Website,
    string? Country, string? City, int? EmployeeCount, Guid? ParentCompanyId);

public sealed record CompanyQuery : PageRequest
{
    public string? Search { get; init; }
    public string? Industry { get; init; }
    public string? Country { get; init; }
    public Guid? ParentCompanyId { get; init; }
    public bool? HasAccount { get; init; }
}

public interface ICompanyService
{
    Task<PagedResult<CompanyDto>> ListAsync(CompanyQuery query, CancellationToken ct);
    Task<CompanyDto> GetAsync(Guid id, CancellationToken ct);
    Task<CompanyDto> CreateAsync(CompanyRequest request, CancellationToken ct);
    Task<CompanyDto> UpdateAsync(Guid id, CompanyRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

internal sealed class CompanyService(ICrmDbContext db, ITransactionRunner tx) : ICompanyService
{
    internal static readonly IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<Company, object?>>> Sorts =
        new Dictionary<string, System.Linq.Expressions.Expression<Func<Company, object?>>>
        {
            ["name"] = c => c.NormalizedName,
            ["industry"] = c => c.Industry,
            ["country"] = c => c.Country,
            ["employees"] = c => c.EmployeeCount,
            ["created"] = c => c.CreatedAt
        };

    private IQueryable<CompanyDto> Project(IQueryable<Company> q) => q.Select(c => new CompanyDto(
        c.Id, c.Name, c.LegalName, c.TaxId, c.Industry, c.Website, c.Country, c.City, c.EmployeeCount,
        c.ParentCompanyId, db.Companies.Where(p => p.Id == c.ParentCompanyId).Select(p => p.Name).FirstOrDefault(),
        db.Contacts.Count(x => x.CompanyId == c.Id),
        db.Accounts.Where(a => a.CompanyId == c.Id).Select(a => (Guid?)a.Id).FirstOrDefault(),
        c.CreatedAt, c.UpdatedAt));

    public async Task<PagedResult<CompanyDto>> ListAsync(CompanyQuery query, CancellationToken ct)
    {
        var q = db.Companies.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            q = q.Where(c => c.NormalizedName.Contains(term)
                             || (c.LegalName != null && c.LegalName.ToUpper().Contains(term))
                             || (c.TaxId != null && c.TaxId.ToUpper().Contains(term))
                             || (c.City != null && c.City.ToUpper().Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(query.Industry)) q = q.Where(c => c.Industry == query.Industry);
        if (!string.IsNullOrWhiteSpace(query.Country)) q = q.Where(c => c.Country == query.Country);
        if (query.ParentCompanyId is { } p) q = q.Where(c => c.ParentCompanyId == p);
        if (query.HasAccount is { } has) q = q.Where(c => db.Accounts.Any(a => a.CompanyId == c.Id) == has);

        return await Project(q.ApplySort(query.Sort, Sorts, c => c.NormalizedName)).ToPagedAsync(query, ct);
    }

    public async Task<CompanyDto> GetAsync(Guid id, CancellationToken ct) =>
        await Project(db.Companies.AsNoTracking().Where(c => c.Id == id)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Company", id);

    public async Task<CompanyDto> CreateAsync(CompanyRequest r, CancellationToken ct)
    {
        await EnsureNameAvailableAsync(r.Name, null, ct);
        if (r.ParentCompanyId is { } parent && !await db.Companies.AnyAsync(c => c.Id == parent, ct))
            throw new NotFoundException("Parent company", parent);

        var company = new Company(r.Name, r.LegalName, r.TaxId, r.Industry, r.Website, r.Country, r.City,
            r.EmployeeCount, r.ParentCompanyId);
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);
        return await GetAsync(company.Id, ct);
    }

    public async Task<CompanyDto> UpdateAsync(Guid id, CompanyRequest r, CancellationToken ct)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id, ct)
                      ?? throw new NotFoundException("Company", id);
        await EnsureNameAvailableAsync(r.Name, id, ct);

        if (r.ParentCompanyId is { } parent)
        {
            if (!await db.Companies.AnyAsync(c => c.Id == parent, ct)) throw new NotFoundException("Parent company", parent);
            // Load the id/parent pairs once; hierarchies are small relative to the company table scan cost.
            var parents = await db.Companies.AsNoTracking()
                .Select(c => new { c.Id, c.ParentCompanyId }).ToDictionaryAsync(c => c.Id, c => c.ParentCompanyId, ct);
            CompanyHierarchyService.EnsureNoCycle(id, parent, pid => parents.GetValueOrDefault(pid));
        }

        company.Update(r.Name, r.LegalName, r.TaxId, r.Industry, r.Website, r.Country, r.City, r.EmployeeCount);
        company.SetParent(r.ParentCompanyId);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await tx.ExecuteAsync(async token =>
        {
            var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id, token)
                          ?? throw new NotFoundException("Company", id);
            if (await db.Accounts.AnyAsync(a => a.CompanyId == id, token))
                throw new ConflictException("The company has a customer account and cannot be deleted.", "company_in_use");
            if (await db.Companies.AnyAsync(c => c.ParentCompanyId == id, token))
                throw new ConflictException("The company has subsidiaries and cannot be deleted.", "company_in_use");

            db.Contacts.RemoveRange(await db.Contacts.Where(c => c.CompanyId == id).ToListAsync(token));
            db.Companies.Remove(company);
            await db.SaveChangesAsync(token);
        }, ct);
    }

    private async Task EnsureNameAvailableAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var normalized = Company.Normalize(name);
        if (await db.Companies.AnyAsync(c => c.NormalizedName == normalized && c.Id != exceptId, ct))
            throw new ConflictException($"A company named '{name.Trim()}' already exists.", "duplicate_company");
    }
}

public sealed class CompanyRequestValidator : AbstractValidator<CompanyRequest>
{
    public CompanyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.LegalName).MaximumLength(300);
        RuleFor(x => x.TaxId).MaximumLength(50);
        RuleFor(x => x.Industry).MaximumLength(100);
        RuleFor(x => x.Website).MaximumLength(300)
            .Must(w => w is null || Uri.TryCreate(w, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https"))
            .WithMessage("Website must be an absolute http(s) URL.");
        RuleFor(x => x.Country).MaximumLength(100);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.EmployeeCount).GreaterThanOrEqualTo(0).When(x => x.EmployeeCount.HasValue);
    }
}

public sealed class CompanyQueryValidator : AbstractValidator<CompanyQuery>
{
    public CompanyQueryValidator() => Include(new PageRequestValidator<CompanyQuery>(CompanyService.Sorts.Keys));
}
