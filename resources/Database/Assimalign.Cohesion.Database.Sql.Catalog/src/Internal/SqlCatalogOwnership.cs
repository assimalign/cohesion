using System;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>Validates the durable ownership metadata shared by catalog objects.</summary>
internal static class SqlCatalogOwnership
{
    internal static void Validate(DatabaseObjectOwner owner, string? schemaName)
    {
        if (owner is not DatabaseObjectOwner.Adhoc and not DatabaseObjectOwner.Schema)
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }

        if (owner == DatabaseObjectOwner.Schema)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        }
        else if (schemaName is not null)
        {
            throw new ArgumentException("An ad-hoc object cannot name an owning compiled schema.", nameof(schemaName));
        }
    }
}
