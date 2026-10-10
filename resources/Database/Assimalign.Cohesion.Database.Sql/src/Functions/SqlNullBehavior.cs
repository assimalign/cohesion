namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// What a function does with a NULL argument: PostgreSQL's <c>STRICT</c> (<c>proisstrict</c>)
/// against <c>CALLED ON NULL INPUT</c>.
/// </summary>
public enum SqlNullBehavior : byte
{
    /// <summary>
    /// Strict, the default. A scalar call with any NULL argument returns NULL without calling the
    /// function; an aggregate skips every row with a NULL argument, so its accumulator never sees one.
    /// </summary>
    ReturnsNullOnNullInput,

    /// <summary>The function is called, or the row added, whatever its arguments are.</summary>
    CalledOnNullInput,
}
