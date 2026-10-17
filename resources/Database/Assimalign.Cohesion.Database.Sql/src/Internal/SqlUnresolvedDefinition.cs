namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A persisted definition bound as unresolved: it calls a function the engine that opened the
/// database does not register, or whose registration no longer accepts the call (owner decision 65
/// of 2026-10-09; the design's §4.10). The database opens and its reads proceed; every write that
/// would evaluate the definition fails with <c>COHSQLE009</c>, and an engine build that declares the
/// database fails with it before the engine accepts work.
/// </summary>
/// <remarks>
/// A definition stores SQL, not a function's identity, so an application release that drops or
/// renames a registered function leaves a CHECK the engine cannot evaluate. Failing the open would
/// be the silent open failure the persisted-definition rule forbids; PostgreSQL likewise fails when
/// a function's implementation is missing at use, not when it loads its catalog
/// (<c>src/backend/utils/fmgr/fmgr.c</c>, "is not in internal lookup table").
/// </remarks>
internal sealed class SqlUnresolvedDefinition
{
    /// <summary>Initializes an unresolved definition.</summary>
    /// <param name="subject">Names the definition, its table included, for example <c>CHECK constraint 'ck' on table 'dbo.t'</c>.</param>
    /// <param name="signature">The call as the definition makes it: the name and the arguments' types, for example <c>slugify(TEXT)</c>.</param>
    /// <param name="reason">Why it does not resolve, completing "calls function 'f(TEXT)', ...".</param>
    internal SqlUnresolvedDefinition(string subject, string signature, string reason)
    {
        Subject = subject;
        Signature = signature;
        Reason = reason;
    }

    /// <summary>Gets the name of the definition, its table included, for a message.</summary>
    internal string Subject { get; }

    /// <summary>Gets the call as the definition makes it, for example <c>slugify(TEXT)</c>.</summary>
    internal string Signature { get; }

    /// <summary>Gets why the call does not resolve, for example <c>which this engine does not register</c>.</summary>
    internal string Reason { get; }

    /// <summary>Describes the definition and its call: <c>CHECK constraint 'ck' on table 'dbo.t' calls function 'f(TEXT)', which ...</c>.</summary>
    /// <returns>The description.</returns>
    internal string Describe() => $"{Subject} calls function '{Signature}', {Reason}";
}
