namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// An <c>AND</c> or <c>OR</c> chain with SQL three-valued logic, its terms evaluated first to last
/// until one decides the result (#1069, #1151).
/// </summary>
internal sealed class SqlBoundLogical : SqlBoundExpression
{
    /// <summary>Initializes a bound chain.</summary>
    /// <param name="isOr"><see langword="true"/> for <c>OR</c>, <see langword="false"/> for <c>AND</c>.</param>
    /// <param name="operands">The bound terms, in order.</param>
    internal SqlBoundLogical(bool isOr, SqlBoundExpression[] operands)
        : base(SqlBoundExpressionKind.Logical)
    {
        IsOr = isOr;
        Operands = operands;
    }

    /// <summary>Gets whether the chain is an <c>OR</c>; otherwise it is an <c>AND</c>.</summary>
    internal bool IsOr { get; }

    /// <summary>Gets the bound terms, in order.</summary>
    internal SqlBoundExpression[] Operands { get; }
}
