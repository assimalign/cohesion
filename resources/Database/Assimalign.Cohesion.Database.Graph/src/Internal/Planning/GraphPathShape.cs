using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphPathShape")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPathShape.DebugView))]
internal sealed class GraphPathShape
{
    private List<GraphPathPrefilter>? _prefilters;

    internal List<List<GqlNodePattern>> Nodes { get; } = [[]];
    internal List<GraphPathSegment> Segments { get; } = [];

    internal IReadOnlyList<GraphPathPrefilter> Prefilters => _prefilters ?? (IReadOnlyList<GraphPathPrefilter>)Array.Empty<GraphPathPrefilter>();
    internal GqlRelationshipPattern? Edge { get; private init; }
    internal long Bound { get; private set; }
    internal bool HasQuantifier { get; private set; }
    internal bool HasUnboundedQuantifier { get; private set; }
    internal int ElementPrefilterCount => Nodes.Sum(nodes => nodes.Count(node => node.Predicate is not null)) +
        (Edge?.Predicate is null ? 0 : 1) + Segments.Sum(segment => segment.Body.ElementPrefilterCount);
    internal int PathPrefilterCount => Prefilters.Count + Segments.Sum(segment => segment.Body.PathPrefilterCount);
    internal bool HasPrefilters => ElementPrefilterCount != 0 || PathPrefilterCount != 0;

    internal static GraphPathShape Create(GqlPathPattern path, bool creating = false, bool parsed = false)
    {
        // Unquantified groups write directly into their owning search shape. Only a
        // quantified body needs a separate shape. Each filter is appended once, on exit,
        // when both boundary offsets are known; no accumulated child lists are copied.
        var result = new GraphPathShape();
        var pending = new Stack<(GqlPathPattern? Path, GqlPathElement? Element, GraphPathShape Shape,
            bool Exit, int Depth, int StartBoundary, GraphPathShape? Parent, GqlQuantifier? Quantifier)>();
        var visited = new HashSet<GqlPathPattern>(ReferenceEqualityComparer.Instance);
        pending.Push((path, null, result, false, 0, 0, null, null));
        while (pending.TryPop(out var item))
        {
            var shape = item.Shape;
            if (item.Element is { } element)
            {
                switch (element)
                {
                    case GqlPathNode { Pattern: { } node }:
                        if (creating && node.Predicate is not null) { throw Invalid(); }
                        shape.Nodes[^1].Add(node);
                        break;
                    case GqlPathRelationship { Pattern: { } edge }:
                        if (creating && edge.Predicate is not null) { throw Invalid(); }
                        CheckQuantifier(edge.Quantifier, zeroAllowed: true, creating);
                        shape.Segments.Add(new GraphPathSegment(new GraphPathShape { Edge = edge, Bound = 1 }, edge.Quantifier));
                        shape.Nodes.Add([]);
                        shape.Bound += edge.Quantifier?.Max ?? edge.Quantifier?.Min ?? 1;
                        shape.HasQuantifier |= edge.Quantifier is not null;
                        shape.HasUnboundedQuantifier |= edge.Quantifier is { Max: null };
                        break;
                    case GqlParenthesizedPathPattern group:
                        if (group.Pattern is null || group.Pattern.Variable is not null) { throw Invalid(); }
                        CheckQuantifier(group.Quantifier, zeroAllowed: false, creating);
                        pending.Push((group.Pattern, null, group.Quantifier is null ? shape : new GraphPathShape(),
                            false, item.Depth + 1, 0, group.Quantifier is null ? null : shape, group.Quantifier));
                        break;
                    default: throw Invalid();
                }
                if (shape.Bound > 64) { throw Invalid(); }
                continue;
            }
            var current = item.Path;
            if (current is null || !parsed && item.Depth > 128) { throw Invalid(); }
            if (item.Exit)
            {
                if (current.Predicate is { } predicate)
                { (shape._prefilters ??= []).Add(new GraphPathPrefilter(predicate, item.StartBoundary, shape.Segments.Count)); }
                if (current.Selector is null && current.Mode == GqlPathMode.Walk && shape.HasUnboundedQuantifier)
                { throw new GraphPlanningException("CK00001", ErrorMessages.UnboundedWalkUnsupported); }
                if (item.Parent is { } parent)
                {
                    if (shape.HasQuantifier || shape.Segments.Count == 0) { throw Invalid(); }
                    var quantifier = item.Quantifier!;
                    parent.Segments.Add(new GraphPathSegment(shape, quantifier));
                    parent.Nodes.Add([]);
                    parent.Bound += shape.Bound * (quantifier.Max ?? quantifier.Min);
                    parent.HasQuantifier = true;
                    parent.HasUnboundedQuantifier |= quantifier.Max is null;
                    if (parent.Bound > 64) { throw Invalid(); }
                }
                continue;
            }
            // Identity checks precede recursive work and record equality: cycles and reused
            // instances cannot amplify caller-owned DAGs. Parsed text uses its parser guard;
            // source-free trees retain their independent depth cap.
            if (!visited.Add(current)) { throw Invalid(); }
            if (current.Selector is { } selector)
            {
                if (creating || !Enum.IsDefined(selector.Kind) || selector.Count <= 0 ||
                    selector.Kind is not (GqlPathSelectorKind.Shortest or GqlPathSelectorKind.ShortestGroups) && selector.Count != 1)
                { throw Invalid(); }
                if (item.Depth != 0) { throw new GraphPlanningException("CK00001", ErrorMessages.NestedPathSelectorUnsupported, selector.Location); }
            }
            if (creating && current.Predicate is not null) { throw Invalid(); }
            if (current.Mode is { } mode)
            {
                if (mode is not (GqlPathMode.Walk or GqlPathMode.Trail or GqlPathMode.Simple or GqlPathMode.Acyclic) || creating) { throw Invalid(); }
                if (item.Depth != 0) { throw new GraphPlanningException("CK00001", ErrorMessages.NestedPathModeUnsupported); }
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
            bool hasStart = false;
            bool needsNode = false;
            foreach (var part in source)
            {
                if (part is GqlPathNode or GqlParenthesizedPathPattern) { hasStart = true; needsNode = false; }
                else if (part is GqlPathRelationship && hasStart && !needsNode) { needsNode = true; }
                else { throw Invalid(); }
            }
            if (!hasStart || needsNode) { throw Invalid(); }
            pending.Push((current, null, shape, true, item.Depth, shape.Segments.Count, item.Parent, item.Quantifier));
            for (int i = source.Count - 1; i >= 0; i--)
            { pending.Push((null, source[i], shape, false, item.Depth, 0, null, null)); }
        }
        return result;
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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPathShape value)
        {
            _ = value;
        }
    }
}
