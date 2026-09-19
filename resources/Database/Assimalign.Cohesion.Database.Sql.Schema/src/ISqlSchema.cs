using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Describes the C# schema for one logical database.
/// </summary>
public interface ISqlSchema
{
    /// <summary>
    /// Gets the logical database name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether migration planning may include destructive operations.
    /// </summary>
    bool AllowsDestructiveChanges { get; }

    /// <summary>
    /// Gets the declared custom types.
    /// </summary>
    IReadOnlyList<ISqlSchemaType> Types { get; }

    /// <summary>
    /// Gets the declared tables.
    /// </summary>
    IReadOnlyList<ISqlSchemaTable> Tables { get; }

    /// <summary>
    /// Gets the declared functions.
    /// </summary>
    IReadOnlyList<ISqlSchemaFunction> Functions { get; }

    /// <summary>
    /// Gets the declared triggers.
    /// </summary>
    IReadOnlyList<ISqlSchemaTrigger> Triggers { get; }

    /// <summary>
    /// Gets the declared database principals.
    /// </summary>
    IReadOnlyList<ISqlSchemaPrincipal> Principals { get; }

    /// <summary>
    /// Gets model-specific extension values.
    /// </summary>
    IReadOnlyList<ISqlSchemaExtension> Extensions { get; }
}
