namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Where a cast may be applied without being written: PostgreSQL's <c>castcontext</c>
/// (<c>src/include/catalog/pg_cast.h</c>), whose values these are.
/// </summary>
/// <remarks>
/// <para>
/// The contexts nest: an implicit cast may also be applied in an assignment and by an explicit
/// <c>CAST</c>, and an assignment cast by an explicit <c>CAST</c>. An explicit <c>CAST</c> applies a
/// cast of any context.
/// </para>
/// <para>
/// <c>CAST</c> admits every context. <c>ALTER COLUMN TYPE</c> admits assignment and implicit
/// casts for both old values and the result of USING. Ordinary INSERT/UPDATE coercion and
/// function overload resolution retain their existing rules. The built-in casts carry
/// PostgreSQL's contexts for the same pairs.
/// </para>
/// </remarks>
public enum SqlCastContext : byte
{
    /// <summary>Applied only by an explicit <c>CAST</c>; the default, and the most restrictive.</summary>
    Explicit = 0,

    /// <summary>Also applied when a value is assigned to a column of the target type.</summary>
    Assignment,

    /// <summary>Also applied in any expression, for example to an argument of a function call.</summary>
    Implicit,
}
