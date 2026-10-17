using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Compares the complete projected tuple for duplicate elimination.</summary>
internal sealed class GraphRowComparer : IEqualityComparer<object?[]>
{
    internal static readonly GraphRowComparer Instance = new();

    private GraphRowComparer() { }

    public bool Equals(object?[]? left, object?[]? right)
    {
        if (ReferenceEquals(left, right)) { return true; }
        if (left is null || right is null || left.Length != right.Length) { return false; }
        for (int i = 0; i < left.Length; i++)
        { if (!GraphValueComparer.Instance.Equals(left[i], right[i])) { return false; } }
        return true;
    }

    public int GetHashCode(object?[] row)
    {
        var hash = new HashCode();
        foreach (var value in row) { hash.Add(GraphValueComparer.Instance.GetHashCode(value)); }
        return hash.ToHashCode();
    }
}
