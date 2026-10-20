using System;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>A persisted SQL namespace, distinct from the compiled schema that may own it.</summary>
public sealed class SqlCatalogNamespace
{
    /// <summary>Creates immutable SQL namespace metadata.</summary>
    /// <param name="name">The namespace's spelling, compared without case.</param>
    /// <param name="owner">The namespace's durable owner.</param>
    /// <param name="owningSchema">The owning compiled schema, or null for an ad-hoc namespace.</param>
    /// <exception cref="ArgumentException">The name or ownership metadata is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The owner is unknown.</exception>
    public SqlCatalogNamespace(string name, DatabaseObjectOwner owner = DatabaseObjectOwner.Adhoc, string? owningSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SqlCatalogOwnership.Validate(owner, owningSchema);
        Name = name;
        Owner = owner;
        OwningSchema = owningSchema;
    }

    /// <summary>Gets the SQL namespace name in its stored spelling.</summary>
    public string Name { get; }

    /// <summary>Gets the durable owner.</summary>
    public DatabaseObjectOwner Owner { get; }

    /// <summary>Gets the owning compiled schema, or null for an ad-hoc namespace.</summary>
    public string? OwningSchema { get; }
}
