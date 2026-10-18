using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed INSERT statement.
/// </summary>
public sealed class SqlInsertExpression : SqlQueryExpression
{
    /// <summary>Gets the statement's non-recursive WITH definitions, in declaration order.</summary>
    public IReadOnlyList<SqlCommonTableExpression> CommonTables { get; internal set; } = [];

    /// <summary>
    /// Initializes a new <see cref="SqlInsertExpression"/>.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="columns">The column list, if specified.</param>
    /// <param name="values">The VALUES rows, if using value lists.</param>
    /// <param name="selectSource">The SELECT source, if using INSERT...SELECT.</param>
    /// <param name="isDefaultValues">Whether the statement inserts one row of defaults.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlInsertExpression(
        SqlTableReference table,
        IReadOnlyList<string>? columns,
        IReadOnlyList<IReadOnlyList<SqlExpression>>? values,
        SqlQueryBodyExpression? selectSource,
        bool isDefaultValues,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Insert, text, location)
    {
        Table = table;
        Columns = columns;
        Values = values;
        SelectSource = selectSource;
        IsDefaultValues = isDefaultValues;
    }

    /// <summary>
    /// Gets the target table reference.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets the column list, if specified.
    /// </summary>
    public IReadOnlyList<string>? Columns { get; }

    /// <summary>
    /// Gets the VALUES rows. Each inner list represents one row of values.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<SqlExpression>>? Values { get; }

    /// <summary>
    /// Gets the SELECT source for INSERT...SELECT, if applicable.
    /// </summary>
    public SqlQueryBodyExpression? SelectSource { get; }

    /// <summary>
    /// Gets whether DEFAULT VALUES inserts one row with every column omitted.
    /// </summary>
    /// <remarks>Both <see cref="Values"/> and <see cref="SelectSource"/> are null for this form.</remarks>
    public bool IsDefaultValues { get; }
}
