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
        GraphMatchClausePlan { Optional: true } => "OptionalMatch",
        GraphMatchClausePlan => "Match",
        GraphPathPlan => "ExpandPath",
        GraphProjectionPlan => "Project",
        GraphDistinctPlan => "Distinct",
        GraphSortKeyPlan => "SortKey",
        GraphSortPagePlan => "SortPage",
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
            ? query.DetachDelete ? "DetachDelete" : "Delete" : plan.MatchClauses.Count == 0 ? "Return" : DescribeOperator(plan);
        Add(root, null, detail: $"filter={plan.MatchClauses.Any(clause => clause.Predicate is not null)}; matches_nothing={plan.MatchesNothing}");
        foreach (var clause in plan.MatchClauses)
        {
            // Preserve the familiar single mandatory MATCH description; multiple clauses and
            // optionals expose their ordered join boundary and local filter/null extension.
            int clauseId = plan.MatchClauses.Count == 1 && !clause.Optional ? 0
                : Add(DescribeOperator(clause), 0, detail: $"filter={clause.Predicate is not null}; matches_nothing={clause.MatchesNothing}; nulls={string.Join(",", clause.Optional ? clause.NewVariables : [])}");
            foreach (var path in clause.Paths)
            {
                int pathId = Add(DescribeOperator(path), clauseId, path.Pattern.Variable);
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
        }
        foreach (var path in query.Creates)
        {
            int parent = Add("InsertPath", 0, path.Variable);
            foreach (var node in path.Nodes) { Add("InsertNode", parent, string.Join("&", node.Labels), detail: node.Variable); }
            foreach (var relationship in path.Relationships)
            { Add("InsertRelationship", parent, relationship.Type, detail: relationship.Direction.ToString()); }
        }
        if (plan.Projections.Count != 0)
        {
            int parent = plan.Distinct is { } distinct
                ? Add(DescribeOperator(distinct), 0, detail: $"keys={distinct.Keys.Count}") : 0;
            foreach (var projection in plan.Projections)
            { Add(DescribeOperator(projection), parent, projection.Name, detail: $"kind={projection.Kind}; expression={GraphProjectionText.Describe(projection.Expression)}"); }
        }
        if (plan.SortPage is { } page)
        {
            int parent = Add(DescribeOperator(page), 0, detail: $"offset={page.Offset}; limit={page.Limit?.ToString(CultureInfo.InvariantCulture) ?? "ALL"}");
            foreach (var key in page.Keys)
            {
                Add(DescribeOperator(key), parent, detail: $"expression={GraphProjectionText.Describe(key.Item.Expression)}; direction={key.Item.Direction}; nulls={key.Item.NullOrdering}");
            }
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
