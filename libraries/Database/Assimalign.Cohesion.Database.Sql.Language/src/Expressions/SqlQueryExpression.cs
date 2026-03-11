namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed SQL expression payload.
/// </summary>
public class SqlQueryExpression : QueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlQueryExpression"/>.
    /// </summary>
    /// <param name="commandType">The inferred top-level command type.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The expression location in source text.</param>
    public SqlQueryExpression(SqlQueryCommandType commandType, string? text, Location? location)
        : base(text, location ?? Location.Create(1, 1, 0, 0))
    {
        CommandType = commandType;
    }

    /// <summary>
    /// Gets the inferred SQL command type.
    /// </summary>
    public SqlQueryCommandType CommandType { get; }
}
