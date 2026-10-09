using Crm.Domain.Common;

namespace Crm.Domain.Services;

public static class CompanyHierarchyService
{
    /// <summary>
    /// Validates that assigning <paramref name="newParentId"/> as the parent of <paramref name="companyId"/> creates no cycle.
    /// <paramref name="parentLookup"/> returns the parent id of a company (or null for roots).
    /// </summary>
    public static void EnsureNoCycle(Guid companyId, Guid newParentId, Func<Guid, Guid?> parentLookup, int maxDepth = 50)
    {
        var current = (Guid?)newParentId;
        for (var depth = 0; current is not null; depth++)
        {
            if (current == companyId)
                throw new DomainException("The selected parent would create a circular company hierarchy.", "invalid_hierarchy");
            if (depth >= maxDepth)
                throw new DomainException("Company hierarchy is too deep.", "invalid_hierarchy");
            current = parentLookup(current.Value);
        }
    }
}
