using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Describes the C# schema for one logical database.
/// </summary>
public interface IDatabaseSchema
{
    /// <summary>
    /// Gets the logical database name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the declared custom types.
    /// </summary>
    IReadOnlyList<IDatabaseSchemaType> Types { get; }

    /// <summary>
    /// Gets the declared tables.
    /// </summary>
    IReadOnlyList<IDatabaseSchemaTable> Tables { get; }

    /// <summary>
    /// Gets the declared functions.
    /// </summary>
    IReadOnlyList<IDatabaseSchemaFunction> Functions { get; }

    /// <summary>
    /// Gets the declared triggers.
    /// </summary>
    IReadOnlyList<IDatabaseSchemaTrigger> Triggers { get; }

    /// <summary>
    /// Gets the declared database principals.
    /// </summary>
    IReadOnlyList<IDatabaseSchemaPrincipal> Principals { get; }
}
