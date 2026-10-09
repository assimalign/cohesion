using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A node the evaluator cannot compute: an unsupported construct, a declared function that does not
/// execute yet, a call whose arguments its signature refuses, a parameter with no supplied value, an
/// unresolvable column, or a literal its type cannot hold. Evaluating it raises a new exception of
/// the same type and message the unbound evaluator raised when it reached the node; binding it
/// raises nothing, so a statement fails only if it evaluates the node (a short-circuited term, an
/// untaken CASE branch, or an empty table never does), exactly as before.
/// </summary>
/// <remarks>
/// The raise is a delegate that reruns the failing step (the literal's parse, the column's
/// resolution, the signature match) or constructs the failure, so each evaluation throws a fresh
/// exception: a bound CHECK predicate is shared by every session, and one exception instance must
/// never be thrown on two threads.
/// </remarks>
internal sealed class SqlBoundFailure : SqlBoundExpression
{
    private readonly Func<object?> _raise;

    /// <summary>Initializes a failing node.</summary>
    /// <param name="raise">Throws the failure; it never returns normally.</param>
    internal SqlBoundFailure(Func<object?> raise)
        : base(SqlBoundExpressionKind.Failure)
    {
        _raise = raise;
    }

    /// <summary>Throws the node's failure.</summary>
    /// <returns>Never returns.</returns>
    internal object? Raise() => _raise();
}
