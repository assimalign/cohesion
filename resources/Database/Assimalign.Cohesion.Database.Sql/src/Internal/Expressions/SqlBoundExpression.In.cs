namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>[NOT] IN</c> over a value list: the bound operand, the bound candidates, and the collation of
/// the operand and each candidate, resolved when the expression was bound.
/// </summary>
internal sealed class SqlBoundIn : SqlBoundExpression
{
    /// <summary>Initializes a bound list membership test.</summary>
    /// <param name="operand">The bound tested value.</param>
    /// <param name="candidates">The bound candidates, in order.</param>
    /// <param name="collations">The collation of the operand and each candidate, by candidate.</param>
    /// <param name="isNegated"><see langword="true"/> for <c>NOT IN</c>.</param>
    internal SqlBoundIn(SqlBoundExpression operand, SqlBoundExpression[] candidates, SqlBoundCollation[] collations, bool isNegated)
        : base(SqlBoundExpressionKind.In)
    {
        Operand = operand;
        Candidates = candidates;
        Collations = collations;
        IsNegated = isNegated;
    }

    /// <summary>Gets the bound tested value.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the bound candidates, in order.</summary>
    internal SqlBoundExpression[] Candidates { get; }

    /// <summary>Gets the collation of the operand and each candidate, by candidate.</summary>
    internal SqlBoundCollation[] Collations { get; }

    /// <summary>Gets whether the test is <c>NOT IN</c>.</summary>
    internal bool IsNegated { get; }
}
