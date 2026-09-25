using System;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Creates immutable database schema declarations from application-owned C#.
/// </summary>
public static class SqlSchema
{
    /// <summary>
    /// Declares and compiles a SQL schema in one step. Equivalent to calling
    /// <see cref="Create(string, Action{ISqlSchemaBuilder})"/> and compiling the result, without the
    /// caller restating the engine model that a SQL schema necessarily has.
    /// </summary>
    /// <param name="name">The database name the schema targets.</param>
    /// <param name="configure">The callback that declares the schema.</param>
    /// <returns>The validated, immutable compiled schema.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="SqlSchemaValidationException">The declaration is not valid.</exception>
    public static SqlCompiledSchema Compile(string name, Action<ISqlSchemaBuilder> configure)
        => SqlSchemaCompiler.Compile(Create(name, configure), EngineModel.Sql);

    /// <summary>
    /// Builds the schema for a logical database.
    /// </summary>
    /// <param name="name">The logical database name.</param>
    /// <param name="configure">Declares the schema.</param>
    /// <returns>The completed schema declaration.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="configure"/> is null.</exception>
    public static ISqlSchema Create(string name, Action<ISqlSchemaBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SqlSchemaBuilder(name);
        configure(builder);
        return builder.Build();
    }
}
