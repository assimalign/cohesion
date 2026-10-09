namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// How the planner types a function's result before any row exists, when the declared
/// <see cref="SqlFunction.ReturnType"/> alone does not say it: a standard-library function whose
/// permissive behavior predates typed signatures (owner decision 67 of 2026-10-09).
/// </summary>
internal enum SqlResultRule : byte
{
    /// <summary>The declared result type, an <see cref="SqlType.AnyElement"/> result taking its arguments' type.</summary>
    Declared,

    /// <summary>
    /// <c>ABS</c>: an exact integer argument widens to BIGINT, and any other argument keeps its type,
    /// a non-number included, as before typed signatures (the call itself fails when it runs).
    /// </summary>
    NumericElement,
}
