using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Configures the grants of a database-scoped principal declared by
/// <see cref="SqlSchemaBuilder.Principal"/>.
/// </summary>
/// <remarks>
/// <b>Shape (concrete-types plan, phase 4, §6.7).</b> A public sealed class with an internal
/// constructor; it replaced the <c>ISqlPrincipalBuilder</c> interface and its internal
/// implementation. The <c>Sdk.Database</c> extractor recognizes its calls by its metadata name.
/// </remarks>
public sealed class SqlPrincipalBuilder
{
    private readonly List<SqlSchemaGrant> _grants = [];
    private readonly string _name;

    /// <summary>
    /// Initializes a new builder for one principal.
    /// </summary>
    /// <param name="name">The name of the principal the builder produces.</param>
    internal SqlPrincipalBuilder(string name)
    {
        _name = name;
    }

    /// <summary>Grants a permission over named schema objects.</summary>
    /// <param name="permission">The granted permission.</param>
    /// <param name="objects">The schema object names.</param>
    /// <exception cref="ArgumentException">No object is provided, or an object name is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="objects"/> or one of its entries is null.</exception>
    public void Grant(SqlPermission permission, params string[] objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Length == 0)
        {
            throw new ArgumentException("At least one schema object is required.", nameof(objects));
        }

        foreach (string schemaObject in objects)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(schemaObject);
        }

        _grants.Add(new SqlSchemaGrant(permission, Array.AsReadOnly((string[])objects.Clone())));
    }

    /// <summary>
    /// Snapshots the grants into the immutable declaration.
    /// </summary>
    /// <returns>The principal declaration; later grants on this builder do not change it.</returns>
    internal SqlSchemaPrincipal Build()
        => new(_name, Array.AsReadOnly(_grants.ToArray()));
}
