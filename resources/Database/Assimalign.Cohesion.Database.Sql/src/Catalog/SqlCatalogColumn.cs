using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The catalog's description of one column: name, shared type identity, nullability,
/// optional canonical default expression, optional collation override, and the domain it was declared
/// with, when one was.
/// </summary>
public sealed class SqlCatalogColumn
{
    /// <summary>
    /// Initializes a new column description.
    /// </summary>
    /// <param name="name">The column name, unique within its table.</param>
    /// <param name="type">The shared type identity and constraints.</param>
    /// <param name="isNullable">Whether the column accepts nulls.</param>
    /// <param name="defaultLiteral">
    /// The default value as the canonical SQL expression the engine renders (for example
    /// <c>'it''s'</c>, <c>5</c> or <c>TRUE</c>), when declared.
    /// </param>
    /// <param name="collation">The string collation override; null inherits the database default.</param>
    /// <param name="domainName">
    /// The name of the domain the column was declared with (phase E3), or null for a column of a
    /// built-in type. <paramref name="type"/> is then the domain's base type, which the row codec
    /// stores; the name lets an engine that opens the database bind the domain again.
    /// </param>
    /// <param name="missingValueLiteral">The frozen canonical literal for rows predating ADD COLUMN; null means a missing field reads NULL.</param>
    /// <param name="identity">The immutable identity metadata, when declared; an identity column is always nonnullable.</param>
    /// <param name="defaultFunctionCalls">Resolved normalized function signatures in expression preorder, retaining repeated calls.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space, a collation is declared for a non-string
    /// column, <paramref name="domainName"/> is empty or white space, or a supplied function identity is blank.
    /// </exception>
    public SqlCatalogColumn(string name, DatabaseTypeInfo type, bool isNullable = true, string? defaultLiteral = null,
        Collation? collation = null, string? domainName = null, IReadOnlyList<string>? defaultFunctionCalls = null, SqlCatalogIdentity? identity = null, string? missingValueLiteral = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        if (collation is not null && type.Type is not (DatabaseType.String or DatabaseType.Character))
        {
            throw new ArgumentException(ErrorMessages.SqlCatalogColumnArgument, nameof(collation));
        }
        if (domainName is not null && string.IsNullOrWhiteSpace(domainName))
        {
            throw new ArgumentException(ErrorMessages.SqlCatalogColumnArgument2, nameof(domainName));
        }

        Identity = identity;
        Name = name;
        Type = type;
        IsNullable = isNullable && identity is null;
        DefaultLiteral = defaultLiteral;
        MissingValueLiteral = missingValueLiteral;
        Collation = collation;
        DomainName = domainName;
        DefaultFunctionCalls = Array.AsReadOnly((defaultFunctionCalls ?? []).ToArray());
        if (DefaultFunctionCalls.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(ErrorMessages.SqlCatalogColumnArgument3, nameof(defaultFunctionCalls));
        }
    }

    /// <summary>Gets the identity facet, or null for an ordinary column.</summary>
    public SqlCatalogIdentity? Identity { get; }

    /// <summary>
    /// Gets the column name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the shared type identity and constraints.
    /// </summary>
    public DatabaseTypeInfo Type { get; }

    /// <summary>
    /// Gets a value indicating whether the column accepts nulls.
    /// </summary>
    public bool IsNullable { get; }

    /// <summary>
    /// Gets the default expression, when one was declared: the canonical SQL text the
    /// engine rendered from the declaration (a string default keeps its quotes, so
    /// <c>DEFAULT 'it''s'</c> is <c>'it''s'</c>), never the text as written.
    /// </summary>
    public string? DefaultLiteral { get; }

    // TYPE keeps non-immutable expression text and pins, but rebinds its assignment
    // cast on load. Ordinary declarations retain their original evaluation contract.
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    internal bool RecastDefault { get; init; }

    /// <summary>Gets the frozen ADD COLUMN value for physically absent fields, independent of the current insert default.</summary>
    public string? MissingValueLiteral { get; }

    /// <summary>Gets each default call's resolved normalized signature in expression preorder.</summary>
    public IReadOnlyList<string> DefaultFunctionCalls { get; }

    /// <summary>
    /// Gets the declared string collation, or null to inherit the database default.
    /// </summary>
    public Collation? Collation { get; }

    /// <summary>
    /// Gets the name of the domain the column was declared with, or null for a column of a
    /// built-in type. A domain can be registered on the engine or defined by SQL in the catalog;
    /// the opening engine resolves either through its merged type environment. <see cref="Type"/>
    /// holds the base type the rows are stored as.
    /// </summary>
    public string? DomainName { get; }
}
