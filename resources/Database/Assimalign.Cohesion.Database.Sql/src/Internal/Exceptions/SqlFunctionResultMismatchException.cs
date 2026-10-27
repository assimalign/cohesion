using System;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

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
/// <c>CS01009</c>, naming the call, instead of the open failing as if the catalog were damaged.
/// </remarks>
internal sealed class SqlFunctionResultMismatchException : DatabaseException
{
    /// <summary>Initializes the refusal.</summary>
    /// <param name="call">The call whose result does not fit.</param>
    /// <param name="returned">The type the call's function returns, as the dialect names it.</param>
    /// <param name="needed">What the position needs, for example <c>BOOLEAN</c> or <c>a value that compares with BIGINT</c>.</param>
    /// <param name="location">The enclosing statement's span if the call has none.</param>
    /// <param name="innerException">The original refusal when a span is attached.</param>
    internal SqlFunctionResultMismatchException(SqlFunctionCallExpression call, string returned, string needed,
        Location? location = null, Exception? innerException = null)
        : base(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            ErrorMessages.CheckFunctionResultMismatch, call.FunctionName, returned, needed), [new Diagnostic
        {
            Code = SqlExecutionException.DatatypeMismatchCode,
            Message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.CheckFunctionResultMismatch, call.FunctionName, returned, needed),
            Severity = DiagnosticSeverity.Error,
            Start = (call.Location ?? location)?.Start,
            End = (call.Location ?? location)?.End,
            Line = (call.Location ?? location)?.StartLine ?? 1,
            Location = (call.Location ?? location) is null ? null : DiagnosticLocation.Absolute,
        }], innerException)
    {
        Call = call;
        Returned = returned;
        Needed = needed;
    }

    internal SqlFunctionResultMismatchException At(Location? location) => Diagnostics[0].Start is not null || location is null
        ? this : new(Call, Returned, Needed, location, this);

    /// <summary>Gets the call whose result does not fit.</summary>
    internal SqlFunctionCallExpression Call { get; }

    /// <summary>Gets the type the call's function returns.</summary>
    internal string Returned { get; }

    /// <summary>Gets what the position needs.</summary>
    internal string Needed { get; }
}
