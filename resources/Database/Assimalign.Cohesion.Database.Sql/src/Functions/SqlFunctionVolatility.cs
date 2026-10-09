namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// How stable a function's result is for the same arguments: what the engine may assume when it
/// folds a call or admits one in a stored definition. The categories are PostgreSQL's
/// (<c>provolatile</c>: <c>i</c>, <c>s</c>, <c>v</c>).
/// </summary>
/// <remarks>
/// The engine cannot verify the declaration. A function marked <see cref="Immutable"/> that is not
/// breaks constant folding and the guarantee a CHECK gives; registration therefore defaults to
/// <see cref="Volatile"/>, so a function must be marked before a CHECK or folding uses it (owner
/// decision 63 of 2026-10-09). <see cref="Volatile"/> is also the zero value, so an uninitialized
/// field or <see langword="default"/> is the safe category, never the most permissive one.
/// </remarks>
public enum SqlFunctionVolatility : byte
{
    /// <summary>
    /// The result can change from one call to the next. The default: never folded, and refused in a
    /// CHECK.
    /// </summary>
    Volatile = 0,

    /// <summary>The same arguments give the same result within one statement.</summary>
    Stable = 1,

    /// <summary>
    /// The same arguments always give the same result, and the call reads nothing else: no table, no
    /// clock, no setting. A call with constant arguments is folded once when the statement is
    /// planned, and only an immutable function may appear in a CHECK.
    /// </summary>
    Immutable = 2,
}
