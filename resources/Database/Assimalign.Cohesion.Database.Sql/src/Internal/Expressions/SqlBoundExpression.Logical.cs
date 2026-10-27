using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// An <c>AND</c> or <c>OR</c> chain with SQL three-valued logic, its terms evaluated first to last
/// until one decides the result (#1069, #1151).
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundLogical")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundLogical.DebugView))]
internal sealed class SqlBoundLogical : SqlBoundExpression
{
    /// <summary>Initializes a bound chain.</summary>
    /// <param name="isOr"><see langword="true"/> for <c>OR</c>, <see langword="false"/> for <c>AND</c>.</param>
    /// <param name="operands">The bound terms, in order.</param>
    /// <param name="location">The source logical expression span, when available.</param>
    internal SqlBoundLogical(bool isOr, SqlBoundExpression[] operands, Location? location = null)
        : base(SqlBoundExpressionKind.Logical)
    {
        IsOr = isOr;
        Operands = operands;
        Location = location;
    }

    /// <summary>Gets whether the chain is an <c>OR</c>; otherwise it is an <c>AND</c>.</summary>
    internal bool IsOr { get; }

    /// <summary>Gets the bound terms, in order.</summary>
    internal SqlBoundExpression[] Operands { get; }

    /// <summary>Gets the source logical expression span.</summary>
    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundLogical value)
        {
            _ = value;
        }
    }
}
