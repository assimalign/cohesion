namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The operation joining two read-only query results.</summary>
public enum GqlCompositeOperator
{
    /// <summary>Returns each structurally distinct row once.</summary>
    Union,
    /// <summary>Concatenates both results, preserving every occurrence.</summary>
    UnionAll,
}
