namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Applies an explicit string collation to an expression without changing its value.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCollateExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlCollateExpression.DebugView))]
public sealed class SqlCollateExpression : SqlExpression
{
    /// <summary>Initializes an explicit collation expression.</summary>
    /// <param name="operand">The expression whose comparison rules are overridden.</param>
    /// <param name="collationName">The validated collation name.</param>
    /// <param name="location">The source location.</param>
    internal SqlCollateExpression(SqlExpression operand, string collationName, Location? location)
        : base(location)
    {
        Operand = operand;
        CollationName = collationName;
        Depth = 1 + operand.Depth;
    }

    /// <summary>Gets the expression whose comparison rules are overridden.</summary>
    public SqlExpression Operand { get; }

    /// <summary>Gets the explicit collation name.</summary>
    public string CollationName { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlCollateExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlCollateExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Operand => _target.Operand;
        public string CollationName => _target.CollationName;
    }
}
