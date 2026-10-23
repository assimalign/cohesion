using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A searched <c>CASE</c> (no input; each condition must be TRUE) or a simple one (each condition is
/// compared with the input under the collation resolved for that pair when the expression was
/// bound). Only the chosen result is evaluated.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundCase")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCase.DebugView))]
internal sealed class SqlBoundCase : SqlBoundExpression
{
    /// <summary>Initializes a bound <c>CASE</c>.</summary>
    /// <param name="input">The bound input of a simple <c>CASE</c>, or null for a searched one.</param>
    /// <param name="whens">The bound <c>WHEN</c> clauses, in order.</param>
    /// <param name="elseResult">The bound <c>ELSE</c> result, or null when there is none.</param>
    internal SqlBoundCase(SqlBoundExpression? input, SqlBoundWhen[] whens, SqlBoundExpression? elseResult, DatabaseType resultType = DatabaseType.Null)
        : base(SqlBoundExpressionKind.Case)
    {
        ResultType = resultType;
        Input = input;
        Whens = whens;
        ElseResult = elseResult;
    }

    internal DatabaseType ResultType { get; }

    /// <summary>Gets the bound input of a simple <c>CASE</c>, or null for a searched one.</summary>
    internal SqlBoundExpression? Input { get; }

    /// <summary>Gets the bound <c>WHEN</c> clauses, in order.</summary>
    internal SqlBoundWhen[] Whens { get; }

    /// <summary>Gets the bound <c>ELSE</c> result, or null when there is none.</summary>
    internal SqlBoundExpression? ElseResult { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCase value)
        {
            _ = value;
        }
    }
}
