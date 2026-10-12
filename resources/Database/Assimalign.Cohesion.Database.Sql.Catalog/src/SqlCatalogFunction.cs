using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>A persisted SQL function definition.</summary>
public sealed class SqlCatalogFunction
{
    /// <summary>Creates immutable function metadata.</summary>
    /// <param name="name">The SQL name.</param>
    /// <param name="signature">The normalized overload identity.</param>
    /// <param name="parameterNames">The ordered parameter names.</param>
    /// <param name="parameterTypes">The ordered declared SQL types.</param>
    /// <param name="returnType">The declared result type.</param>
    /// <param name="volatility">The volatility keyword.</param>
    /// <param name="isStrict">Whether NULL arguments bypass execution.</param>
    /// <param name="body">The canonical SQL expression body.</param>
    /// <param name="dependencies">The function signature and domain dependency identities.</param>
    /// <param name="owner">The durable owner.</param>
    /// <param name="owningSchema">The owning schema, or null for an ad-hoc definition.</param>
    /// <exception cref="ArgumentException">A required name or ownership value is invalid.</exception>
    /// <exception cref="ArgumentNullException">A required collection is null.</exception>
    public SqlCatalogFunction(string name, string signature, IReadOnlyList<string> parameterNames, IReadOnlyList<string> parameterTypes, string returnType, string volatility, bool isStrict, string body, IReadOnlyList<string> dependencies, DatabaseObjectOwner owner, string? owningSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        ArgumentException.ThrowIfNullOrWhiteSpace(returnType);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentNullException.ThrowIfNull(parameterNames);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (volatility is not ("IMMUTABLE" or "STABLE" or "VOLATILE") || parameterNames.Count != parameterTypes.Count ||
            parameterNames.Any(string.IsNullOrWhiteSpace) || parameterTypes.Any(string.IsNullOrWhiteSpace) || dependencies.Any(string.IsNullOrWhiteSpace) ||
            parameterNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameterNames.Count)
        {
            throw new ArgumentException("Invalid SQL function signature, volatility or dependencies.");
        }
        SqlCatalogOwnership.Validate(owner, owningSchema);
        Name = name;
        Signature = signature;
        ArgumentNullException.ThrowIfNull(parameterNames);
        ParameterNames = Array.AsReadOnly(parameterNames.ToArray());
        ArgumentNullException.ThrowIfNull(parameterTypes);
        ParameterTypes = Array.AsReadOnly(parameterTypes.ToArray());
        ReturnType = returnType;
        Volatility = volatility;
        IsStrict = isStrict;
        Body = body;
        ArgumentNullException.ThrowIfNull(dependencies);
        Dependencies = Array.AsReadOnly(dependencies.ToArray());
        Owner = owner;
        OwningSchema = owningSchema;
    }

    /// <summary>Gets the SQL name.</summary>
    public string Name { get; }

    /// <summary>Gets the normalized overload identity.</summary>
    public string Signature { get; }

    /// <summary>Gets the ordered parameter names.</summary>
    public IReadOnlyList<string> ParameterNames { get; }

    /// <summary>Gets the ordered declared SQL types.</summary>
    public IReadOnlyList<string> ParameterTypes { get; }

    /// <summary>Gets the declared result type.</summary>
    public string ReturnType { get; }

    /// <summary>Gets the volatility keyword.</summary>
    public string Volatility { get; }

    /// <summary>Gets whether NULL arguments bypass execution.</summary>
    public bool IsStrict { get; }

    /// <summary>Gets the canonical SQL expression body.</summary>
    public string Body { get; }

    /// <summary>Gets the function signature and domain dependency identities.</summary>
    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>Gets the durable owner.</summary>
    public DatabaseObjectOwner Owner { get; }

    /// <summary>Gets the owning schema, or null for an ad-hoc definition.</summary>
    public string? OwningSchema { get; }
}
