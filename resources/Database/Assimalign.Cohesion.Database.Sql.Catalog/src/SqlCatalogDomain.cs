using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>A persisted SQL domain definition.</summary>
public sealed class SqlCatalogDomain
{
    /// <summary>Creates immutable domain metadata.</summary>
    /// <param name="name">The SQL name.</param>
    /// <param name="baseType">The canonical base SQL type.</param>
    /// <param name="defaultLiteral">The canonical default literal, if any.</param>
    /// <param name="isNotNull">The whether NULL is refused.</param>
    /// <param name="constraintName">The CHECK name, if any.</param>
    /// <param name="checkExpression">The canonical CHECK predicate over VALUE, if any.</param>
    /// <param name="dependencies">The function signature dependency identities.</param>
    /// <param name="owner">The durable owner.</param>
    /// <param name="owningSchema">The owning schema, or null for an ad-hoc definition.</param>
    /// <exception cref="ArgumentException">A required name or ownership value is invalid.</exception>
    /// <exception cref="ArgumentNullException">A required collection is null.</exception>
    public SqlCatalogDomain(string name, string baseType, string? defaultLiteral, bool isNotNull, string? constraintName, string? checkExpression, IReadOnlyList<string> dependencies, DatabaseObjectOwner owner, string? owningSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseType);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (dependencies.Any(string.IsNullOrWhiteSpace) || constraintName is not null && string.IsNullOrWhiteSpace(checkExpression))
        {
            throw new ArgumentException("Invalid SQL domain constraint or dependencies.");
        }
        SqlCatalogOwnership.Validate(owner, owningSchema);
        Name = name;
        BaseType = baseType;
        DefaultLiteral = defaultLiteral;
        IsNotNull = isNotNull;
        ConstraintName = constraintName;
        CheckExpression = checkExpression;
        ArgumentNullException.ThrowIfNull(dependencies);
        Dependencies = Array.AsReadOnly(dependencies.ToArray());
        Owner = owner;
        OwningSchema = owningSchema;
    }

    /// <summary>Gets the SQL name.</summary>
    public string Name { get; }

    /// <summary>Gets the canonical base SQL type.</summary>
    public string BaseType { get; }

    /// <summary>Gets the canonical default literal, if any.</summary>
    public string? DefaultLiteral { get; }

    /// <summary>Gets the whether NULL is refused.</summary>
    public bool IsNotNull { get; }

    /// <summary>Gets the CHECK name, if any.</summary>
    public string? ConstraintName { get; }

    /// <summary>Gets the canonical CHECK predicate over VALUE, if any.</summary>
    public string? CheckExpression { get; }

    /// <summary>Gets the function signature dependency identities.</summary>
    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>Gets the durable owner.</summary>
    public DatabaseObjectOwner Owner { get; }

    /// <summary>Gets the owning schema, or null for an ad-hoc definition.</summary>
    public string? OwningSchema { get; }
}
