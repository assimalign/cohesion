namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The supported MATCH path selection rules.</summary>
public enum GqlPathSelectorKind
{
    /// <summary>Selects one path per ordered endpoint pair.</summary>
    Any,
    /// <summary>Selects every admissible path per ordered endpoint pair.</summary>
    All,
    /// <summary>Selects one path of minimum length per ordered endpoint pair.</summary>
    AnyShortest,
    /// <summary>Selects every minimum-length path; equivalent to SHORTEST 1 GROUPS.</summary>
    AllShortest,
    /// <summary>Selects up to the given number of paths, in ascending length order.</summary>
    Shortest,
    /// <summary>Selects every path in up to the given number of distinct ascending lengths.</summary>
    ShortestGroups,
}
