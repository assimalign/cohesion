namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Specifies one index key column's effective direction and NULL placement.</summary>
public enum CompiledSchemaIndexKeyOrder
{
    /// <summary>Ascending values, with NULL before all values (the default).</summary>
    AscendingNullsFirst = 0,
    /// <summary>Ascending values, with NULL after all values.</summary>
    AscendingNullsLast = 1,
    /// <summary>Descending values, with NULL before all values.</summary>
    DescendingNullsFirst = 2,
    /// <summary>Descending values, with NULL after all values (the DESC default).</summary>
    DescendingNullsLast = 3,
}
