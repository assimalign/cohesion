namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed DROP INDEX statement:
/// <c>DROP INDEX [IF EXISTS] name ON table</c>. The table qualifier is required —
/// index names are scoped per table in the declared dialect.
/// </summary>
public sealed class SqlDropIndexExpression : SqlQueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlDropIndexExpression"/>.
    /// </summary>
    /// <param name="indexName">The name of the index being dropped.</param>
    /// <param name="table">The table the index belongs to.</param>
    /// <param name="ifExists">Whether IF EXISTS was specified.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlDropIndexExpression(
        string indexName,
        SqlTableReference table,
        bool ifExists,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Drop, text, location)
    {
        IndexName = indexName;
        Table = table;
        IfExists = ifExists;
    }

    /// <summary>
    /// Gets the name of the index being dropped.
    /// </summary>
    public string IndexName { get; }

    /// <summary>
    /// Gets the table the index belongs to.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets whether IF EXISTS was specified.
    /// </summary>
    public bool IfExists { get; }
}
