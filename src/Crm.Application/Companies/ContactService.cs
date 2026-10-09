using Crm.Application.Common;
using Crm.Domain.Companies;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Companies;

public sealed record ContactDto(Guid Id, Guid CompanyId, string CompanyName, string FirstName, string LastName,
    string FullName, string Email, string? Phone, string? JobTitle, bool IsPrimary, DateTimeOffset CreatedAt);

public sealed record ContactRequest(string FirstName, string LastName, string Email, string? Phone, string? JobTitle,
    bool IsPrimary);

public sealed record ContactQuery : PageRequest
{
    public string? Search { get; init; }
    public Guid? CompanyId { get; init; }
    public bool? IsPrimary { get; init; }
}

public interface IContactService
{
    Task<PagedResult<ContactDto>> ListAsync(ContactQuery query, CancellationToken ct);
    Task<ContactDto> GetAsync(Guid id, CancellationToken ct);
    Task<ContactDto> CreateAsync(Guid companyId, ContactRequest request, CancellationToken ct);
    Task<ContactDto> UpdateAsync(Guid id, ContactRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

internal sealed class ContactService(ICrmDbContext db, ITransactionRunner tx) : IContactService
{
    internal static readonly IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<Contact, object?>>> Sorts =
        new Dictionary<string, System.Linq.Expressions.Expression<Func<Contact, object?>>>
        {
            ["name"] = c => c.LastName,
            ["email"] = c => c.Email,
            ["company"] = c => c.Company.Name,
            ["created"] = c => c.CreatedAt
        };

    private static IQueryable<ContactDto> Project(IQueryable<Contact> q) => q.Select(c => new ContactDto(
        c.Id, c.CompanyId, c.Company.Name, c.FirstName, c.LastName, c.FirstName + " " + c.LastName, c.Email,
        c.Phone, c.JobTitle, c.IsPrimary, c.CreatedAt));

    public async Task<PagedResult<ContactDto>> ListAsync(ContactQuery query, CancellationToken ct)
    {
        var q = db.Contacts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            q = q.Where(c => c.FirstName.ToLower().Contains(term) || c.LastName.ToLower().Contains(term)
                             || c.Email.Contains(term) || c.Company.Name.ToLower().Contains(term)
                             || (c.JobTitle != null && c.JobTitle.ToLower().Contains(term)));
        }
        if (query.CompanyId is { } cid) q = q.Where(c => c.CompanyId == cid);
        if (query.IsPrimary is { } p) q = q.Where(c => c.IsPrimary == p);
        return await Project(q.ApplySort(query.Sort, Sorts, c => c.LastName)).ToPagedAsync(query, ct);
    }

    public async Task<ContactDto> GetAsync(Guid id, CancellationToken ct) =>
        await Project(db.Contacts.AsNoTracking().Where(c => c.Id == id)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Contact", id);

    public async Task<ContactDto> CreateAsync(Guid companyId, ContactRequest r, CancellationToken ct)
    {
        var id = await tx.ExecuteAsync(async token =>
        {
            if (!await db.Companies.AnyAsync(c => c.Id == companyId, token))
                throw new NotFoundException("Company", companyId);
            var email = r.Email.Trim().ToLowerInvariant();
            if (await db.Contacts.AnyAsync(c => c.CompanyId == companyId && c.Email == email, token))
                throw new ConflictException($"A contact with email '{email}' already exists for this company.", "duplicate_contact");

            var contact = new Contact(companyId, r.FirstName, r.LastName, r.Email, r.Phone, r.JobTitle);
            db.Contacts.Add(contact);
            await ApplyPrimaryAsync(contact, r.IsPrimary, token);
            await db.SaveChangesAsync(token);
            return contact.Id;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<ContactDto> UpdateAsync(Guid id, ContactRequest r, CancellationToken ct)
    {
        await tx.ExecuteAsync(async token =>
        {
            var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == id, token)
                          ?? throw new NotFoundException("Contact", id);
            var email = r.Email.Trim().ToLowerInvariant();
            if (await db.Contacts.AnyAsync(c => c.CompanyId == contact.CompanyId && c.Email == email && c.Id != id, token))
                throw new ConflictException($"A contact with email '{email}' already exists for this company.", "duplicate_contact");
            contact.Update(r.FirstName, r.LastName, r.Email, r.Phone, r.JobTitle);
            await ApplyPrimaryAsync(contact, r.IsPrimary, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == id, ct)
                      ?? throw new NotFoundException("Contact", id);
        db.Contacts.Remove(contact);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Invariant: at most one primary contact per company.</summary>
    private async Task ApplyPrimaryAsync(Contact contact, bool isPrimary, CancellationToken ct)
    {
        if (isPrimary)
        {
            var others = await db.Contacts.Where(c => c.CompanyId == contact.CompanyId && c.IsPrimary && c.Id != contact.Id)
                .ToListAsync(ct);
            foreach (var other in others) other.SetPrimary(false);
        }
        contact.SetPrimary(isPrimary);
    }
}

public sealed class ContactRequestValidator : AbstractValidator<ContactRequest>
{
    public ContactRequestValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Phone).MaximumLength(50);
        RuleFor(x => x.JobTitle).MaximumLength(150);
    }
}

public sealed class ContactQueryValidator : AbstractValidator<ContactQuery>
{
    public ContactQueryValidator() => Include(new PageRequestValidator<ContactQuery>(ContactService.Sorts.Keys));
}
