using Crm.Domain.Common;

namespace Crm.Domain.Companies;

/// <summary>A legal/organisational entity. Companies may form a hierarchy (parent/subsidiary).</summary>
public class Company : Entity
{
    private Company() { }

    public Company(string name, string? legalName, string? taxId, string? industry, string? website,
        string? country, string? city, int? employeeCount, Guid? parentCompanyId)
    {
        Apply(name, legalName, taxId, industry, website, country, city, employeeCount);
        ParentCompanyId = parentCompanyId;
    }

    public string Name { get; private set; } = null!;
    /// <summary>Upper-cased name used for case-insensitive uniqueness.</summary>
    public string NormalizedName { get; private set; } = null!;
    public string? LegalName { get; private set; }
    public string? TaxId { get; private set; }
    public string? Industry { get; private set; }
    public string? Website { get; private set; }
    public string? Country { get; private set; }
    public string? City { get; private set; }
    public int? EmployeeCount { get; private set; }
    public Guid? ParentCompanyId { get; private set; }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    public void Update(string name, string? legalName, string? taxId, string? industry, string? website,
        string? country, string? city, int? employeeCount) =>
        Apply(name, legalName, taxId, industry, website, country, city, employeeCount);

    public void SetParent(Guid? parentCompanyId)
    {
        if (parentCompanyId == Id)
            throw new DomainException("A company cannot be its own parent.", "invalid_hierarchy");
        ParentCompanyId = parentCompanyId;
    }

    private void Apply(string name, string? legalName, string? taxId, string? industry, string? website,
        string? country, string? city, int? employeeCount)
    {
        Name = Guard.Required(name, "Name", 200);
        NormalizedName = Normalize(Name);
        LegalName = Guard.Optional(legalName, "Legal name", 300);
        TaxId = Guard.Optional(taxId, "Tax id", 50);
        Industry = Guard.Optional(industry, "Industry", 100);
        Website = Guard.Optional(website, "Website", 300);
        Country = Guard.Optional(country, "Country", 100);
        City = Guard.Optional(city, "City", 100);
        if (employeeCount is < 0) throw new DomainException("Employee count cannot be negative.", "invalid_value");
        EmployeeCount = employeeCount;
    }
}
