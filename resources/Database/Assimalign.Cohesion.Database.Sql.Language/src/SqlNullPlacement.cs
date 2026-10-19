namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Specifies NULL placement for an ORDER BY key, independently of its direction.
/// </summary>
public enum SqlNullPlacement
{
    /// <summary>Uses the dialect default: NULL first ascending and last descending.</summary>
    Default,

    /// <summary>Places NULL before non-NULL values.</summary>
    First,

    /// <summary>Places NULL after non-NULL values.</summary>
    Last,
}
