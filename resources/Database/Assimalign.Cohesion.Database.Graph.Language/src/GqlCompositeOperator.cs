namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The operation joining two read-only query results.</summary>
public enum GqlCompositeOperator
{
    /// <summary>Returns each structurally distinct row once.</summary>
    Union,
    /// <summary>Concatenates both results, preserving every occurrence.</summary>
    UnionAll,
    /// <summary>Returns each left row absent from the right result once.</summary>
    Except,
    /// <summary>Returns max(left count minus right count, zero) occurrences.</summary>
    ExceptAll,
    /// <summary>Returns each row present in both results once.</summary>
    Intersect,
    /// <summary>Returns min(left count, right count) occurrences.</summary>
    IntersectAll,
    /// <summary>Returns the whole left result when nonempty, otherwise evaluates the right.</summary>
    Otherwise,
}
