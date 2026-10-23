using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed class GraphPathShape
{
    internal List<List<GqlNodePattern>> Nodes { get; } = [[]];
    internal List<GraphPathSegment> Segments { get; } = [];
    internal GqlRelationshipPattern? Edge { get; private init; }
    internal long Bound { get; private set; }
    internal bool HasQuantifier { get; private set; }

    internal static GraphPathShape Create(GqlPathPattern path, bool creating = false, bool parsed = false)
    {
        // Inspect identity before any recursive work or record equality. Refuse reused instances
        // as well as cycles, so caller-owned DAGs cannot multiply work exponentially.
        // Parsed text has already passed the parser stack guard; only direct ASTs have a depth cap.
        var pending = new Stack<(GqlPathPattern? Path, bool Exit, int Depth)>();
        var visited = new HashSet<GqlPathPattern>(ReferenceEqualityComparer.Instance);
        var shapes = new Dictionary<GqlPathPattern, GraphPathShape>(ReferenceEqualityComparer.Instance);
        pending.Push((path, false, 0));
        while (pending.TryPop(out var item))
        {
            var current = item.Path;
            if (current is null || !parsed && item.Depth > 128) { throw Invalid(); }
            if (!item.Exit)
            {
                if (!visited.Add(current)) { throw Invalid(); }
                pending.Push((current, true, item.Depth));
                if (current.Elements is { } elements)
                {
                    foreach (var element in elements.Reverse())
                    {
                        if (element is GqlParenthesizedPathPattern group)
                        { pending.Push((group.Pattern, false, item.Depth + 1)); }
                    }
                }
                continue;
            }
            if (current.Nodes is null || current.Relationships is null) { throw Invalid(); }
            bool grouped = current.Elements?.Any(element => element is GqlParenthesizedPathPattern) == true;
            if (creating && grouped) { throw Invalid(); }
            if (grouped && (current.Nodes.Count != 0 || current.Relationships.Count != 0)) { throw Invalid(); }
            if (!grouped && (current.Nodes.Count == 0 || current.Relationships.Count != current.Nodes.Count - 1 ||
                current.Nodes.Any(node => node is null) || current.Relationships.Any(edge => edge is null))) { throw Invalid(); }
            var source = current.Elements;
            if (source is null)
            {
                var derived = new List<GqlPathElement>();
                for (int i = 0; i < current.Nodes.Count; i++)
                {
                    derived.Add(new GqlPathNode(current.Nodes[i]));
                    if (i < current.Relationships.Count) { derived.Add(new GqlPathRelationship(current.Relationships[i])); }
                }
                source = derived;
            }
            if (source.Count == 0) { throw Invalid(); }
            if (!grouped)
            {
                if (source.Count != current.Nodes.Count + current.Relationships.Count) { throw Invalid(); }
                for (int i = 0; i < source.Count; i++)
                {
                    if (i % 2 == 0 ? source[i] is not GqlPathNode node || node.Pattern != current.Nodes[i / 2]
                        : source[i] is not GqlPathRelationship edge || edge.Pattern != current.Relationships[i / 2]) { throw Invalid(); }
                }
            }
            var shape = new GraphPathShape();
            bool hasStart = false;
            bool needsNode = false;
            foreach (var element in source)
            {
                switch (element)
                {
                    case GqlPathNode { Pattern: { } node }:
                        shape.Nodes[^1].Add(node);
                        hasStart = true;
                        needsNode = false;
                        break;
                    case GqlPathRelationship { Pattern: { } edge } when hasStart && !needsNode:
                        CheckQuantifier(edge.Quantifier, zeroAllowed: true, creating);
                        shape.Segments.Add(new GraphPathSegment(new GraphPathShape { Edge = edge, Bound = 1 }, edge.Quantifier));
                        shape.Nodes.Add([]);
                        shape.Bound += edge.Quantifier?.Max ?? edge.Quantifier?.Min ?? 1;
                        shape.HasQuantifier |= edge.Quantifier is not null;
                        needsNode = true;
                        break;
                    case GqlParenthesizedPathPattern group:
                        if (group.Pattern.Variable is not null) { throw Invalid(); }
                        var body = shapes[group.Pattern];
                        CheckQuantifier(group.Quantifier, zeroAllowed: false, creating);
                        if (group.Quantifier is { } quantifier)
                        {
                            if (body.HasQuantifier || body.Segments.Count == 0) { throw Invalid(); }
                            shape.Segments.Add(new GraphPathSegment(body, quantifier));
                            shape.Nodes.Add([]);
                            shape.Bound += body.Bound * (quantifier.Max ?? quantifier.Min);
                            shape.HasQuantifier = true;
                        }
                        else
                        {
                            shape.Nodes[^1].AddRange(body.Nodes[0]);
                            for (int i = 0; i < body.Segments.Count; i++)
                            { shape.Segments.Add(body.Segments[i]); shape.Nodes.Add(new List<GqlNodePattern>(body.Nodes[i + 1])); }
                            shape.Bound += body.Bound;
                            shape.HasQuantifier |= body.HasQuantifier;
                        }
                        hasStart = true;
                        needsNode = false;
                        break;
                    default: throw Invalid();
                }
                if (shape.Bound > 64) { throw Invalid(); }
            }
            if (!hasStart || needsNode) { throw Invalid(); }
            shapes[current] = shape;
        }
        return shapes[path];
    }

    internal IEnumerable<(string Name, bool Node)> Bindings()
    {
        if (Edge is { Variable: { } edgeVariable }) { yield return (edgeVariable, false); }
        for (int i = 0; i < Nodes.Count; i++)
        {
            foreach (var node in Nodes[i]) { if (node.Variable is { } name) { yield return (name, true); } }
            if (i < Segments.Count)
            { foreach (var binding in Segments[i].Body.Bindings()) { yield return binding; } }
        }
    }

    private static void CheckQuantifier(GqlQuantifier? quantifier, bool zeroAllowed, bool creating)
    {
        if (quantifier is null) { return; }
        if (creating || quantifier.Min < 0 || quantifier.Max < quantifier.Min || !zeroAllowed && quantifier.Max == 0 ||
            quantifier.Min > 64 || quantifier.Max > 64) { throw Invalid(); }
    }

    private static GraphPlanningException Invalid() => new("CG05001", ErrorMessages.PathShapeInvalid);
}
