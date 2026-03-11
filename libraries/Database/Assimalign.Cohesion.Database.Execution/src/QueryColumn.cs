using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Describes a column within a query result set.
/// </summary>
public sealed class QueryColumn
{
    /// <summary>
    /// Gets the column name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the zero-based ordinal position of the column.
    /// </summary>
    public required int Ordinal { get; init; }

    /// <summary>
    /// Gets the data type of the column.
    /// </summary>
    public required DatabaseType Type { get; init; }

    /// <summary>
    /// Gets whether the column allows null values.
    /// </summary>
    public bool IsNullable { get; init; }
}
