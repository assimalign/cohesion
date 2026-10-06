using System;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// An immutable database schema declaration made from application-owned C#: the schema of one
/// logical database, as <see cref="Create"/> recorded it, ready to <see cref="Compile()"/>.
/// </summary>
/// <remarks>
/// <para>
/// The declaration is opaque. Its tables, types, functions, triggers, principals and extensions
/// are internal records the compiler reads; the public result of a declaration is the
/// <see cref="SqlCompiledSchema"/> that <see cref="Compile()"/> returns. Most applications call
/// <see cref="Compile(string, Action{SqlSchemaBuilder})"/>, which declares and compiles in one step.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, §6.7).</b> A public sealed class with an internal
/// constructor. It replaced the static <c>SqlSchema</c> factory, whose <c>Create</c> returned the
/// <c>ISqlSchema</c> interface over positional records, and the public static
/// <c>SqlSchemaCompiler</c>, which is now internal: a public positional record cannot close its
/// primary constructor or its <c>with</c> clone, so the nine declaration records stay internal
/// behind this type. The <c>Sdk.Database</c> extractor finds <see cref="Create"/> and
/// <see cref="Compile(string, Action{SqlSchemaBuilder})"/> calls by this type's name, which a
/// non-static class keeps.
/// </para>
/// </remarks>
public sealed class SqlSchema
{
    private readonly SqlSchemaDeclaration _declaration;

    /// <summary>
    /// Initializes a schema over a completed declaration.
    /// </summary>
    /// <param name="declaration">The declaration <see cref="SqlSchemaBuilder"/> completed.</param>
    internal SqlSchema(SqlSchemaDeclaration declaration)
    {
        _declaration = declaration;
    }

    /// <summary>
    /// Gets the logical database name the schema targets.
    /// </summary>
    public string Name => _declaration.Name;

    /// <summary>
    /// Gets the declaration the schema compiler reads.
    /// </summary>
    internal SqlSchemaDeclaration Declaration => _declaration;

    /// <summary>
    /// Validates the declaration and lowers it into the SQL engine's stable compiled schema.
    /// </summary>
    /// <returns>The validated, immutable compiled schema, with a deterministic content hash.</returns>
    /// <exception cref="SqlSchemaValidationException">The declaration is not valid.</exception>
    public SqlCompiledSchema Compile() => SqlSchemaCompiler.Compile(this, EngineModel.Sql);

    /// <summary>
    /// Declares and compiles a SQL schema in one step. Equivalent to calling
    /// <see cref="Create(string, Action{SqlSchemaBuilder})"/> and compiling the result.
    /// </summary>
    /// <param name="name">The database name the schema targets.</param>
    /// <param name="configure">The callback that declares the schema.</param>
    /// <returns>The validated, immutable compiled schema.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="SqlSchemaValidationException">The declaration is not valid.</exception>
    public static SqlCompiledSchema Compile(string name, Action<SqlSchemaBuilder> configure)
        => Create(name, configure).Compile();

    /// <summary>
    /// Builds the schema for a logical database.
    /// </summary>
    /// <param name="name">The logical database name.</param>
    /// <param name="configure">Declares the schema.</param>
    /// <returns>The completed schema declaration.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="configure"/> is null.</exception>
    public static SqlSchema Create(string name, Action<SqlSchemaBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SqlSchemaBuilder(name);
        configure(builder);
        return new SqlSchema(builder.Build());
    }
}
