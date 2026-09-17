using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Creates immutable database schema declarations from application-owned C#.
/// </summary>
public static class DatabaseSchema
{
    /// <summary>
    /// Builds the schema for a logical database.
    /// </summary>
    /// <param name="name">The logical database name.</param>
    /// <param name="configure">Declares the schema.</param>
    /// <returns>The completed schema declaration.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="configure"/> is null.</exception>
    public static IDatabaseSchema Create(string name, Action<IDatabaseSchemaBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new DatabaseSchemaBuilder(name);
        configure(builder);
        return builder.Build();
    }
}
