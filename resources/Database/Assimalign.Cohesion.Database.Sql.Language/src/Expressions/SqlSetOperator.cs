namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>The SQL binary set operators.</summary>
public enum SqlSetOperator
{
    /// <summary>Combines rows from both inputs.</summary>
    Union,
    /// <summary>Retains left rows absent from the right input.</summary>
    Except,
    /// <summary>Retains rows present in both inputs; binds tighter than UNION and EXCEPT.</summary>
    Intersect,
}
