using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Refuses a CHECK predicate in which an application function's declared result does not fit where
/// the predicate uses it: a BOOLEAN predicate position, a comparison with an operand of a type it
/// does not compare with, or an arithmetic operand that is not a number.
/// </summary>
/// <remarks>
/// DDL reports it as the reason the CHECK is refused. When a stored predicate raises it, the
/// application changed the function's registration after the predicate was stored (owner decision
/// 65): the predicate binds as unresolved and each write that would evaluate it fails with
/// <c>COHSQLE009</c>, naming the call, instead of the open failing as if the catalog were damaged.
/// </remarks>
internal sealed class SqlFunctionResultMismatchException : DatabaseException
{
    /// <summary>Initializes the refusal.</summary>
    /// <param name="call">The call whose result does not fit.</param>
    /// <param name="returned">The type the call's function returns, as the dialect names it.</param>
    /// <param name="needed">What the position needs, for example <c>BOOLEAN</c> or <c>a value that compares with BIGINT</c>.</param>
    internal SqlFunctionResultMismatchException(SqlFunctionCallExpression call, string returned, string needed)
        : base($"Function '{call.FunctionName}' returns {returned}, where the CHECK needs {needed}.")
    {
        Call = call;
        Returned = returned;
        Needed = needed;
    }

    /// <summary>Gets the call whose result does not fit.</summary>
    internal SqlFunctionCallExpression Call { get; }

    /// <summary>Gets the type the call's function returns.</summary>
    internal string Returned { get; }

    /// <summary>Gets what the position needs.</summary>
    internal string Needed { get; }
}
