using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>[NOT] IN</c> over a group of candidates without column references, with the common
/// comparison type and collations resolved when the expression was bound.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundIn")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundIn.DebugView))]
internal sealed class SqlBoundIn : SqlBoundExpression
{
    /// <summary>Initializes a bound list membership test.</summary>
    /// <param name="operand">The bound tested value.</param>
    /// <param name="candidates">The bound candidates, in order.</param>
    /// <param name="collations">The collation of the operand and each candidate, by candidate.</param>
    /// <param name="comparisonType">The common comparison identity, without assignment modifiers.</param>
    /// <param name="isNegated"><see langword="true"/> for <c>NOT IN</c>.</param>
    internal SqlBoundIn(SqlBoundExpression operand, SqlBoundExpression[] candidates, SqlBoundCollation[] collations,
        DatabaseType comparisonType, bool isNegated)
        : base(SqlBoundExpressionKind.In)
    {
        // Fixed comparands acquire CHAR identity once at binding, not once per row.
        // Computed TEXT/VARCHAR values receive the same identity during evaluation.
        if (comparisonType == DatabaseType.Character)
        {
            operand = CharacterComparand(operand);
            for (int index = 0; index < candidates.Length; index++)
            {
                candidates[index] = CharacterComparand(candidates[index]);
            }
        }
        Operand = operand;
        Candidates = candidates;
        Collations = collations;
        ComparisonType = comparisonType;
        IsNegated = isNegated;
    }

    /// <summary>Gets the bound tested value.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the bound candidates, in order.</summary>
    internal SqlBoundExpression[] Candidates { get; }

    /// <summary>Gets the collation of the operand and each candidate, by candidate.</summary>
    internal SqlBoundCollation[] Collations { get; }

    /// <summary>Gets the common comparison identity; CHAR coercion never pads or truncates a comparand.</summary>
    internal DatabaseType ComparisonType { get; }

    /// <summary>Gets whether the test is <c>NOT IN</c>.</summary>
    internal bool IsNegated { get; }

    private static SqlBoundExpression CharacterComparand(SqlBoundExpression expression) => expression switch
    {
        SqlBoundParameterConstant { Value: string text } => new SqlBoundParameterConstant(new SqlCharacter(text)),
        SqlBoundConstant { Value: string text } => new SqlBoundConstant(new SqlCharacter(text)),
        SqlBoundParameter { Value: string text } parameter => new SqlBoundParameter(parameter.Name, new SqlCharacter(text)),
        _ => expression,
    };

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundIn value)
        {
            _ = value;
        }
    }
}
