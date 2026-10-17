using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed partial class GraphPlanner
{
    internal static string DescribeOperator(object plan) => plan switch
    {
        GraphPlan => "Match",
        GraphPathPlan => "ExpandPath",
        GraphAnchor { Property: not null } => "IndexSeek",
        GraphAnchor { Label: not null } => "LabelScan",
        GraphAnchor => "NodeScan",
        _ => throw new UnreachableException(),
    };

    internal DatabasePlanDescription Describe(GraphPlan plan)
    {
        var nodes = new List<DatabasePlanNode>();
        var query = plan.Query;
        string root = query.Creates.Count > 0 ? "Insert" : query.DeleteVariables.Count > 0
            ? query.DetachDelete ? "DetachDelete" : "Delete" : DescribeOperator(plan);
        Add(root, null, detail: $"filter={query.Predicate is not null}; matches_nothing={plan.MatchesNothing}");
        foreach (var path in plan.Matches)
        {
            int pathId = Add(DescribeOperator(path), 0, path.Pattern.Variable);
            var anchor = path.Anchor;
            string? index = anchor.Property is null ? null : $"{anchor.Label}({anchor.Property})";
            if (anchor.Property is not null && _database.Catalog.FindLabel(anchor.Label!, _snapshot) is { } label)
            {
                index = _database.Catalog.GetIndexes(label.Id, _snapshot)
                    .Where(candidate => candidate.PropertyKey == anchor.Property)
                    .OrderBy(candidate => candidate.Name, StringComparer.Ordinal).FirstOrDefault().Name ?? index;
            }
            int parent = Add(DescribeOperator(anchor),
                pathId, anchor.Label, index, $"node={anchor.NodeIndex}; property={anchor.Property}; value={Value(anchor.Value)}");
            // The executor expands right of the anchor, then left, in this exact order.
            foreach (int edge in Enumerable.Range(anchor.NodeIndex, path.Pattern.Relationships.Count - anchor.NodeIndex)
                .Concat(Enumerable.Range(0, anchor.NodeIndex).Reverse()))
            {
                var relationship = path.Pattern.Relationships[edge];
                parent = Add("Expand", parent, relationship.Type,
                    detail: $"edge={edge}; direction={relationship.Direction}; labels={relationship.LabelExpression}");
            }
        }
        foreach (var path in query.Creates)
        {
            int parent = Add("InsertPath", 0, path.Variable);
            foreach (var node in path.Nodes) { Add("InsertNode", parent, string.Join("&", node.Labels), detail: node.Variable); }
            foreach (var relationship in path.Relationships)
            { Add("InsertRelationship", parent, relationship.Type, detail: relationship.Direction.ToString()); }
        }
        return DatabasePlanDescription.Create(nodes, plan.Warnings);

        int Add(string name, int? parent, string? target = null, string? index = null, string? detail = null)
        {
            int id = nodes.Count;
            nodes.Add(new DatabasePlanNode { NodeId = id, ParentId = parent, Operator = name, Target = target, Index = index, Detail = detail });
            return id;
        }
    }

    private static string Value(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };
}
