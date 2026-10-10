using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Builds a database schema from application-owned CLR types and expression trees: the builder
/// <see cref="SqlSchema.Create"/> and <see cref="SqlSchema.Compile(string, Action{SqlSchemaBuilder})"/>
/// hand to the declaring callback.
/// </summary>
/// <remarks>
/// <b>Shape (concrete-types plan, phase 4, §6.7).</b> A public sealed class with an internal
/// constructor; it replaced the <c>ISqlSchemaBuilder</c> interface and its internal
/// implementation. The <c>Sdk.Database</c> extractor recognizes this builder's calls by its
/// metadata name, so a rename of the type changes the extractor's constant in the same commit.
/// The C# lambda <c>Function</c> and <c>Trigger</c> declarations and the <c>Extension</c> value
/// were deleted (owner decision 57 of 2026-10-09): their canonical text was never executed. A SQL
/// engine's functions are registered on its engine builder instead.
/// </remarks>
public sealed class SqlSchemaBuilder
{
    private readonly List<SqlSchemaType> _types = [];
    private readonly List<SqlSchemaTable> _tables = [];
    private readonly List<SqlSchemaPrincipal> _principals = [];
    private readonly string _name;
    private bool _allowsDestructiveChanges;

    /// <summary>
    /// Initializes a new builder for one logical database.
    /// </summary>
    /// <param name="name">The name of the schema the builder produces.</param>
    internal SqlSchemaBuilder(string name)
    {
        _name = name;
    }

    /// <summary>Allows migrations that remove or rewrite existing data.</summary>
    public void AllowDestructiveChanges() => _allowsDestructiveChanges = true;

    /// <summary>Declares a custom database type.</summary>
    /// <typeparam name="T">The CLR type represented by the declaration.</typeparam>
    /// <param name="configure">Configures the type.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <remarks>
    /// The declaration compiles, but a SQL engine has no DDL for a custom type yet: its Build
    /// refuses a declared database whose schema declares one, before any file is touched.
    /// </remarks>
    public void Type<T>(Action<SqlTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SqlTypeBuilder(typeof(T));
        configure(builder);
        _types.Add(builder.Build());
    }

    /// <summary>Declares a table whose row shape is <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The table row type.</typeparam>
    /// <param name="configure">Configures the table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public void Table<T>(Action<SqlTableBuilder<T>> configure)
        => Table(typeof(T).Name, configure);

    /// <summary>Declares a named table whose row shape is <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The table row type.</typeparam>
    /// <param name="name">The stable database table name.</param>
    /// <param name="configure">Configures the table.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="configure"/> is null.</exception>
    public void Table<T>(string name, Action<SqlTableBuilder<T>> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SqlTableBuilder<T>(name);
        configure(builder);
        _tables.Add(builder.Build());
    }

    /// <summary>Declares a database-scoped principal.</summary>
    /// <param name="name">The principal name.</param>
    /// <param name="configure">Configures the principal's grants.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="configure"/> is null.</exception>
    /// <remarks>
    /// The declaration compiles, but a SQL engine has no principal or grant DDL yet: its Build
    /// refuses a declared database whose schema declares a principal, before any file is touched
    /// (owner decision 58 of 2026-10-09).
    /// </remarks>
    public void Principal(string name, Action<SqlPrincipalBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SqlPrincipalBuilder(name);
        configure(builder);
        _principals.Add(builder.Build());
    }

    /// <summary>
    /// Snapshots what the builder recorded into the immutable declaration.
    /// </summary>
    /// <returns>The completed declaration; later calls on this builder do not change it.</returns>
    internal SqlSchemaDeclaration Build()
        => new(
            _allowsDestructiveChanges,
            Snapshot(_types),
            Snapshot(_tables),
            Snapshot(_principals),
            _name);

    private static ReadOnlyCollection<T> Snapshot<T>(List<T> values)
        => Array.AsReadOnly(values.ToArray());
}
