using Crm.Domain.Common;

namespace Crm.Domain.Companies;

public class Contact : Entity
{
    private Contact() { }

    public Contact(Guid companyId, string firstName, string lastName, string email, string? phone, string? jobTitle)
    {
        CompanyId = companyId;
        Apply(firstName, lastName, email, phone, jobTitle);
    }

    public Guid CompanyId { get; private set; }
    public Company Company { get; private set; } = null!;
    public string FirstName { get; private set; } = null!;
    public string LastName { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string? Phone { get; private set; }
    public string? JobTitle { get; private set; }
    public bool IsPrimary { get; private set; }

    public string FullName => $"{FirstName} {LastName}";

    public void Update(string firstName, string lastName, string email, string? phone, string? jobTitle) =>
        Apply(firstName, lastName, email, phone, jobTitle);

    public void SetPrimary(bool isPrimary) => IsPrimary = isPrimary;

    private void Apply(string firstName, string lastName, string email, string? phone, string? jobTitle)
    {
        FirstName = Guard.Required(firstName, "First name", 100);
        LastName = Guard.Required(lastName, "Last name", 100);
        Email = Guard.Required(email, "Email", 320).ToLowerInvariant();
        Phone = Guard.Optional(phone, "Phone", 50);
        JobTitle = Guard.Optional(jobTitle, "Job title", 150);
    }
}
