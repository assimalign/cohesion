namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A searched <c>CASE</c> (no input; each condition must be TRUE) or a simple one (each condition is
/// compared with the input under the collation resolved for that pair when the expression was
/// bound). Only the chosen result is evaluated.
/// </summary>
internal sealed class SqlBoundCase : SqlBoundExpression
{
    /// <summary>Initializes a bound <c>CASE</c>.</summary>
    /// <param name="input">The bound input of a simple <c>CASE</c>, or null for a searched one.</param>
    /// <param name="whens">The bound <c>WHEN</c> clauses, in order.</param>
    /// <param name="elseResult">The bound <c>ELSE</c> result, or null when there is none.</param>
    internal SqlBoundCase(SqlBoundExpression? input, SqlBoundWhen[] whens, SqlBoundExpression? elseResult)
        : base(SqlBoundExpressionKind.Case)
    {
        Input = input;
        Whens = whens;
        ElseResult = elseResult;
    }

    /// <summary>Gets the bound input of a simple <c>CASE</c>, or null for a searched one.</summary>
    internal SqlBoundExpression? Input { get; }

    /// <summary>Gets the bound <c>WHEN</c> clauses, in order.</summary>
    internal SqlBoundWhen[] Whens { get; }

    /// <summary>Gets the bound <c>ELSE</c> result, or null when there is none.</summary>
    internal SqlBoundExpression? ElseResult { get; }
}

/// <summary>One bound <c>WHEN</c> clause of a <see cref="SqlBoundCase"/>.</summary>
/// <param name="Condition">The bound condition, or the bound value a simple <c>CASE</c> compares with its input.</param>
/// <param name="Result">The bound result.</param>
/// <param name="Collation">The collation of the input and the value, for a simple <c>CASE</c>; unused for a searched one.</param>
internal readonly record struct SqlBoundWhen(SqlBoundExpression Condition, SqlBoundExpression Result, SqlBoundCollation Collation);
