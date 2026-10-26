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
        GraphStagePlan => "Stage",
        GqlSetStage => "Set",
        GqlRemoveStage => "Remove",
        GraphMatchClausePlan { Optional: true } => "OptionalMatch",
        GraphMatchClausePlan => "Match",
        GraphPathPlan => "ExpandPath",
        GraphProjectionPlan => "Project",
        GraphDistinctPlan => "Distinct",
        GraphGroupingPlan => "GroupAggregate",
        GraphSortKeyPlan => "SortKey",
        GraphSortPagePlan => "SortPage",
        GraphAnchor { RelationshipTypes: not null } => "RelationshipTypeScan",
        GraphAnchor { Property: not null } => "IndexSeek",
        GraphAnchor { Label: not null } => "LabelScan",
        GraphAnchor => "NodeScan",
        _ => throw new UnreachableException(),
    };

    internal DatabasePlanDescription Describe(GraphPlan plan, GraphPlanExecutor.ExpansionBudget? budget = null)
    {
        var nodes = new List<DatabasePlanNode>();
        bool pipeline = plan.Stages.Any(item => item.Stage is GqlLetStage or GqlForStage or GqlFilterStage or GqlNextStage or GqlSetStage or GqlRemoveStage or GqlReturnStage { IsWith: true }) ||
            plan.Stages.Select((item, i) => item.Stage is GqlMatchStage && plan.Stages.Take(i).Any(earlier => earlier.Stage is GqlInsertStage or GqlDeleteStage)).Any(value => value) ||
            plan.Stages.Count(item => item.Stage is GqlInsertStage or GqlDeleteStage) > 1;
        if (pipeline)
        {
            Add("Pipeline", null, detail: $"stages={plan.Stages.Count}; writes={plan.Writes}");
            foreach (var item in plan.Stages)
            {
                int stageRoot = nodes.Count;
                Append(item.Plan, 0, StageName(item.Stage));
                if (item.Stage is GqlSetStage set)
                {
                    foreach (var change in set.Items)
                    { Add("UpdateEntity", stageRoot, change.Variable, detail: change switch
                        { GqlSetPropertyItem property => $"set {property.Property} = {GraphProjectionText.Describe(property.Value)}",
                            GqlSetPropertiesItem map => map.Merge ? "merge properties" : "replace properties",
                            GqlSetLabelsItem labels => $"add labels {string.Join(",", labels.Labels)}", _ => "set" }); }
                }
                if (item.Stage is GqlRemoveStage remove)
                {
                    foreach (var change in remove.Items)
                    { Add("UpdateEntity", stageRoot, change.Variable, detail: change switch
                        { GqlRemovePropertyItem property => $"remove property {property.Property}",
                            GqlRemoveLabelsItem labels => $"remove labels {string.Join(",", labels.Labels)}", _ => "remove" }); }
                }
            }
        }
        else { Append(plan, null, null); }
        return DatabasePlanDescription.Create(nodes, plan.Warnings);

        void Append(GraphPlan plan, int? parentId, string? rootName)
        {
            var query = plan.Query;
            string root = query.Creates.Count > 0 ? "Insert" : query.DeleteVariables.Count > 0
                ? query.DetachDelete ? "DetachDelete" : "Delete" : plan.MatchClauses.Count == 0 ? "Return" : DescribeOperator(plan);
            int rootId = Add(rootName ?? root, parentId, detail: $"filter={plan.MatchClauses.Any(clause => clause.Predicate is not null)}; matches_nothing={plan.MatchesNothing}");
            foreach (var clause in plan.MatchClauses)
            {
                // Preserve the familiar single mandatory MATCH description; multiple clauses and
                // optionals expose their ordered join boundary and local filter/null extension.
                int clauseId = plan.MatchClauses.Count == 1 && !clause.Optional ? rootId
                    : Add(DescribeOperator(clause), rootId, detail: $"filter={clause.Predicate is not null}; matches_nothing={clause.MatchesNothing}; nulls={string.Join(",", clause.Optional ? clause.NewVariables : [])}");
                foreach (var path in clause.Paths)
                {
                    int pathId = Add(DescribeOperator(path), clauseId, path.Pattern.Variable, detail: $"mode={path.Mode}");
                    var anchor = path.Anchor;
                    string? index = anchor.Property is null ? null : $"{anchor.Label}({anchor.Property})";
                    if (anchor.Property is not null && _database.Catalog.FindLabel(anchor.Label!, _snapshot) is { } label)
                    {
                        index = _database.Catalog.GetIndexes(label.Id, _snapshot)
                            .Where(candidate => candidate.PropertyKey == anchor.Property)
                            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal).FirstOrDefault().Name ?? index;
                    }
                    var shape = path.Shape ?? GraphPathShape.Create(path.Pattern);
                    int parent = anchor.RelationshipTypes is { } types
                        ? Add(DescribeOperator(anchor), pathId, string.Join("|", types),
                            detail: $"edge={anchor.NodeIndex}; direction={shape.Segments[anchor.NodeIndex].Body.Edge!.Direction}; postings=(type_id,relationship); combine=union; physical=visited_postings; viable=accepted_hops; physical_limit={budget?.WorkLimit ?? GraphPlanExecutor.ExpansionBudget.PhysicalLimit}",
                            estimate: anchor.EstimatedRows)
                        : Add(DescribeOperator(anchor), pathId, anchor.UnionLabels is { } union ? string.Join("|", union) : anchor.Label, index,
                            $"node={anchor.NodeIndex}; property={anchor.Property}; value={Value(anchor.Value)}" +
                            (anchor.UnionLabels is null ? "" : "; combine=union"), anchor.EstimatedRows);
                    // The posting supplies its edge directly; only the remaining edges expand.
                    int rightStart = anchor.NodeIndex + (anchor.RelationshipTypes is null ? 0 : 1);
                    foreach (int edge in Enumerable.Range(rightStart, shape.Segments.Count - rightStart)
                        .Concat(Enumerable.Range(0, anchor.NodeIndex).Reverse()))
                    {
                        var segment = shape.Segments[edge];
                        if (segment.Quantifier is { } quantifier)
                        {
                            parent = Add("Expand", parent, segment.Body.Edge?.Type,
                                detail: $"edge={edge}; quantifier={{{quantifier.Min},{quantifier.Max?.ToString(CultureInfo.InvariantCulture) ?? ""}}}; body_hops={segment.Body.Bound}; mode={path.Mode}; {AdjacencyDetail(segment.Body, edge >= anchor.NodeIndex, budget?.WorkLimit ?? GraphPlanExecutor.ExpansionBudget.PhysicalLimit)}");
                        }
                        else
                        {
                            var relationship = segment.Body.Edge!;
                            parent = Add("Expand", parent, relationship.Type,
                                detail: $"edge={edge}; direction={relationship.Direction}; labels={relationship.LabelExpression}; {HopDetail(relationship, edge >= anchor.NodeIndex, budget?.WorkLimit ?? GraphPlanExecutor.ExpansionBudget.PhysicalLimit)}");
                        }
                    }
                }
            }
            foreach (var path in query.Creates)
            {
                int parent = Add("InsertPath", rootId, path.Variable);
                foreach (var node in path.Nodes) { Add("InsertNode", parent, string.Join("&", node.Labels), detail: node.Variable); }
                foreach (var relationship in path.Relationships)
                { Add("InsertRelationship", parent, relationship.Type, detail: relationship.Direction.ToString()); }
            }
            if (plan.Projections.Count != 0)
            {
                if (plan.Grouping is { } grouping)
                { Add(DescribeOperator(grouping), rootId, detail: $"keys={grouping.Keys.Count}; aggregates={string.Join(",", grouping.Aggregates.Select(GraphProjectionText.Describe))}"); }
                int parent = plan.Distinct is { } distinct
                    ? Add(DescribeOperator(distinct), rootId, detail: $"keys={distinct.Keys.Count}") : rootId;
                foreach (var projection in plan.Projections)
                { Add(DescribeOperator(projection), parent, projection.Name, detail: $"kind={projection.Kind}; expression={GraphProjectionText.Describe(projection.Expression)}"); }
            }
            if (plan.SortPage is { } page)
            {
                int parent = Add(DescribeOperator(page), rootId, detail: $"offset={page.Offset}; limit={page.Limit?.ToString(CultureInfo.InvariantCulture) ?? "ALL"}");
                foreach (var key in page.Keys)
                {
                    Add(DescribeOperator(key), parent, detail: $"expression={GraphProjectionText.Describe(key.Item.Expression)}; direction={key.Item.Direction}; nulls={key.Item.NullOrdering}");
                }
            }
        }

        int Add(string name, int? parent, string? target = null, string? index = null, string? detail = null, long? estimate = null)
        {
            int id = nodes.Count;
            nodes.Add(new DatabasePlanNode { NodeId = id, ParentId = parent, Operator = name, Target = target, Index = index, Detail = detail, EstimatedRows = estimate });
            return id;
        }
    }

    private static string AdjacencyDetail(GraphPathShape shape, bool forward, long workLimit)
        => shape.Edge is { } edge ? HopDetail(edge, forward, workLimit)
            : string.Join(" | ", shape.Segments.Select(segment => AdjacencyDetail(segment.Body, forward, workLimit)));

    private static string HopDetail(GqlRelationshipPattern relationship, bool forward, long workLimit)
        => $"adjacency=(node,direction,type_id,relationship); seek_direction={GraphLabelEvaluator.IncidentDirection(relationship, forward)}; type_filter={relationship.LabelExpression?.ToString() ?? relationship.Type ?? "%"}; physical=visited_candidates; viable=accepted_hops; physical_limit={workLimit}";

    private static string StageName(GqlStage stage) => stage switch
    {
        GqlMatchStage { Clause.Optional: true } => "OptionalMatch",
        GqlMatchStage => "Match",
        GqlLetStage => "Let",
        GqlFilterStage => "Filter",
        GqlForStage { IsUnwind: true } => "Unwind",
        GqlForStage => "For",
        GqlInsertStage => "Insert",
        GqlSetStage => "Set",
        GqlRemoveStage => "Remove",
        GqlDeleteStage { Detach: true } => "DetachDelete",
        GqlDeleteStage => "Delete",
        GqlReturnStage { IsWith: true } => "With",
        GqlReturnStage => "Return",
        GqlNextStage => "Next",
        _ => throw new UnreachableException(),
    };

    private static string Value(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };
}
