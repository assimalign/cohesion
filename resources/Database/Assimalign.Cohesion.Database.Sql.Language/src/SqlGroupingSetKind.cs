namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Identifies the expansion of a GROUP BY grouping element.</summary>
public enum SqlGroupingSetKind
{
    /// <summary>Combines all children into one grouping set; no children denotes ().</summary>
    Composite,
    /// <summary>Concatenates the expansions of its children.</summary>
    Sets,
    /// <summary>Expands successive prefixes, including the empty prefix.</summary>
    Rollup,
    /// <summary>Expands every combination of children.</summary>
    Cube,
}
