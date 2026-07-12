namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed SQL expression payload.
/// </summary>
public class SqlQueryExpression : QueryExpression
{
    private string? _statementText;

    /// <summary>
    /// Initializes a new <see cref="SqlQueryExpression"/>.
    /// </summary>
    /// <param name="commandType">The inferred top-level command type.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The expression location in source text.</param>
    public SqlQueryExpression(SqlQueryCommandType commandType, string? text, Location? location)
        : base(text, location ?? Location.Create(1, 1, 0, 0))
    {
        _statementText = text;
        CommandType = commandType;
    }

    /// <summary>
    /// Gets the raw statement text the expression was parsed from, when available.
    /// </summary>
    public override string? Text => _statementText ?? base.Text;

    /// <summary>
    /// Gets the inferred SQL command type.
    /// </summary>
    public SqlQueryCommandType CommandType { get; }

    /// <summary>
    /// Stamps the raw statement text after parsing (the parser owns the source span;
    /// expression constructors do not).
    /// </summary>
    internal void SetStatementText(string text) => _statementText = text;
}
