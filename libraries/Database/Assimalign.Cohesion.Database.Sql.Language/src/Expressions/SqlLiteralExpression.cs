namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a literal value in a SQL expression (string, integer, float, null, or boolean).
/// </summary>
public sealed class SqlLiteralExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlLiteralExpression"/>.
    /// </summary>
    /// <param name="value">The text representation of the literal.</param>
    /// <param name="literalType">The kind of literal.</param>
    /// <param name="location">The source location.</param>
    internal SqlLiteralExpression(string value, SqlLiteralType literalType, Location? location)
        : base(location)
    {
        Value = value;
        LiteralType = literalType;
    }

    /// <summary>
    /// Gets the text representation of the literal value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the kind of literal (string, integer, float, null, or boolean).
    /// </summary>
    public SqlLiteralType LiteralType { get; }
}
