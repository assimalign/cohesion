namespace Assimalign.Cohesion.Database.Language.Sql;

/// <summary>
/// Classifies the type of a SQL JOIN clause.
/// </summary>
public enum SqlJoinType
{
    /// <summary>An <c>INNER JOIN</c>.</summary>
    Inner,

    /// <summary>A <c>LEFT OUTER JOIN</c>.</summary>
    LeftOuter,

    /// <summary>A <c>RIGHT OUTER JOIN</c>.</summary>
    RightOuter,

    /// <summary>A <c>FULL OUTER JOIN</c>.</summary>
    FullOuter,

    /// <summary>A <c>CROSS JOIN</c>.</summary>
    Cross,
}
