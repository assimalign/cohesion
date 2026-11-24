using System.Collections.Generic;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

public sealed class Node
{
    public int Value { get; set; }

    public Node? Next { get; set; }
}

/// <summary>Refers to itself through a list and a dictionary.</summary>
public sealed class TreeNode
{
    public string Name { get; set; } = string.Empty;

    public List<TreeNode> Children { get; set; } = new();

    public Dictionary<string, TreeNode>? Index { get; set; }
}

/// <summary>Uses a recursive type in two places.</summary>
public sealed class NodePair
{
    public Node? First { get; set; }

    public Node? Second { get; set; }
}

/// <summary>A collection of itself: recursion with no object type in the cycle.</summary>
public sealed class Nested : List<Nested>
{
}

public sealed class NestedPair
{
    public Nested? Left { get; set; }

    public Nested? Right { get; set; }
}
