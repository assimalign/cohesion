namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents an untyped or typed literal value in a SQL expression.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlLiteralExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlLiteralExpression.DebugView))]
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
    /// Gets the unquoted literal value; temporal, interval and binary values use parser-normalized canonical text.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the kind of literal.
    /// </summary>
    public SqlLiteralType LiteralType { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlLiteralExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlLiteralExpression value)
        {
            _target = value;
        }

        public string Value => _target.Value;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlLiteralType LiteralType => _target.LiteralType;
    }
}
