using System;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

/// <summary>Validates the durable ownership metadata shared by catalog objects.</summary>
internal static class SqlCatalogOwnership
{
    internal static void Validate(DatabaseObjectOwner owner, string? owningSchema)
    {
        if (owner is not DatabaseObjectOwner.Adhoc and not DatabaseObjectOwner.Schema)
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }

        if (owner == DatabaseObjectOwner.Schema)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(owningSchema);
        }
        else if (owningSchema is not null)
        {
            throw new ArgumentException("An ad-hoc object cannot name an owning compiled schema.", nameof(owningSchema));
        }
    }
}
