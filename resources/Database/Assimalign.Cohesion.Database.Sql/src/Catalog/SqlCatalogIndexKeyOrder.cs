namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>Specifies one index key column's effective direction and NULL placement.</summary>
public enum SqlCatalogIndexKeyOrder
{
    /// <summary>Ascending values, with NULL after all values (the default).</summary>
    AscendingNullsLast = 0,
    /// <summary>Ascending values, with NULL before all values.</summary>
    AscendingNullsFirst = 1,
    /// <summary>Descending values, with NULL after all values.</summary>
    DescendingNullsLast = 2,
    /// <summary>Descending values, with NULL before all values (the DESC default).</summary>
    DescendingNullsFirst = 3,
}
