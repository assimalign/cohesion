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
        GraphCorrelatedSubplan => "CorrelatedSubplan",
        GraphStagePlan => "Stage",
        GqlSetStage => "Set",
        GqlRemoveStage => "Remove",
        GraphMatchClausePlan { Optional: true } => "OptionalMatch",
        GraphMatchClausePlan => "Match",
        GraphPathSelectionPlan => "SelectPaths",
        GraphPathPlan { Selection: not null } => "SelectPaths",
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
        bool pipeline = plan.Stages.Any(item => item.Stage is GqlMergeStage or GqlLetStage or GqlForStage or GqlFilterStage or GqlNextStage or GqlSetStage or GqlRemoveStage or GqlReturnStage { IsWith: true }) ||
            plan.Stages.Select((item, i) => item.Stage is GqlMatchStage && plan.Stages.Take(i).Any(earlier => earlier.Stage is GqlInsertStage or GqlDeleteStage)).Any(value => value) ||
            plan.Stages.Count(item => item.Stage is GqlInsertStage or GqlDeleteStage) > 1;
        if (pipeline)
        {
            Add("Pipeline", null, detail: $"stages={plan.Stages.Count}; writes={plan.Writes}");
            foreach (var item in plan.Stages)
            {
                int stageRoot = nodes.Count;
                Append(item.Plan, 0, StageName(item.Stage));
                if (item.Stage is GqlMergeStage merge)
                {
                    int create = Add("OnCreate", stageRoot, detail: "create every unbound part; undirected creation follows source order");
                    int match = Add("OnMatch", stageRoot, detail: "complete path; per input row; pattern lock and freshness check");
                    foreach (var change in merge.OnCreate) { Add("UpdateEntity", create, change.Variable, detail: "set"); }
                    foreach (var change in merge.OnMatch) { Add("UpdateEntity", match, change.Variable, detail: "set"); }
                }
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
        if (plan.FunctionBindings is GraphCorrelatedBindings correlated)
        {
            var pending = new Stack<(GqlExpression? Owner, int Parent)>();
            pending.Push((null, 0));
            while (pending.TryPop(out var scope))
            {
                foreach (var pair in correlated.Subplans)
                {
                    if (!ReferenceEquals(pair.Value.Parent, scope.Owner)) { continue; }
                    bool collect = pair.Key is GqlPatternComprehensionExpression;
                    int apply = Add(collect ? "CollectApply" : correlated.Anti?.Contains(pair.Key) == true ? "AntiSemiApply" : "SemiApply", scope.Parent,
                        detail: $"correlated={string.Join(",", pair.Value.CorrelatedVariables)}; first_match={!collect}; materialize={collect}");
                    int inner = nodes.Count;
                    Append(pair.Value.Plan, apply, "CorrelatedSubplan");
                    pending.Push((pair.Key, inner));
                }
            }
        }
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
                int pathParent = clause.Paths.Any(path => path.Selection is not null) && clause.Predicate is { } postfilter
                    ? Add("PostFilter", clauseId, detail: GraphProjectionText.Describe(postfilter)) : clauseId;
                foreach (var path in clause.Paths)
                {
                    var selection = path.Selection;
                    int pathId = Add(DescribeOperator(path), pathParent, path.Pattern.Variable,
                        detail: selection is null ? $"mode={path.Mode}"
                            : $"selector={selection.Selector}; partition=endpoint_pair; per_input=True; mode={selection.Mode}; count={selection.Count?.ToString(CultureInfo.InvariantCulture) ?? "ALL"}; count_unit={(selection.CountLengthGroups ? "distinct_lengths" : "paths")}; order={(selection.OrderByLength ? "length," : "")}identity; start={string.Join(",", selection.StartVariables)}; end={string.Join(",", selection.EndVariables)}; bound_endpoints={string.Join(",", selection.BoundEndpoints)}; execution=iterative_deepening");
                    var anchor = path.Anchor;
                    string? index = anchor.Property is null ? null : $"{anchor.Label}({anchor.Property})";
                    if (anchor.Property is not null && _database.Catalog.FindLabel(anchor.Label!, _snapshot) is { } label)
                    {
                        index = _database.Catalog.GetIndexes(label.Id, _snapshot)
                            .Where(candidate => candidate.PropertyKey == anchor.Property)
                            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal).FirstOrDefault().Name ?? index;
                    }
                    var shape = path.Shape ?? GraphPathShape.Create(path.Pattern);
                    if (selection is not null || shape.HasPrefilters)
                    {
                        pathId = Add("PathSearch", pathId, detail: $"element_predicates={shape.ElementPrefilterCount}; path_predicates={shape.PathPrefilterCount}; work_limit={budget?.WorkLimit ?? GraphPlanExecutor.ExpansionBudget.PhysicalLimit}; state_and_output_memory_limit={budget?.Memory.Limit ?? 268435456}; work_refusal=CG05004; memory_refusal=CG05020; statement_pairs={budget?.SelectorPairs ?? 0}; statement_states={budget?.SelectorStates ?? 0}; statement_candidates={budget?.SelectorCandidates ?? 0}; statement_selected={budget?.SelectorSelected ?? 0}");
                        DescribePrefilters(shape, pathId, "root");
                    }
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

        void DescribePrefilters(GraphPathShape shape, int parent, string scope)
        {
            foreach (var filter in shape.Prefilters)
            { Add("PathPrefilter", parent, detail: $"scope={scope}; boundaries={filter.StartBoundary}..{filter.EndBoundary}; expression={GraphProjectionText.Describe(filter.Predicate)}"); }
            foreach (var node in shape.Nodes.SelectMany(nodes => nodes))
            {
                if (node.LabelExpression is not null || node.Labels.Count != 0 || node.Properties.Count != 0 || node.Predicate is not null)
                { Add("ElementPrefilter", parent, node.Variable, detail: $"scope={scope}; labels={node.LabelExpression?.ToString() ?? string.Join("&", node.Labels)}; properties={string.Join(",", node.Properties.Keys)}; predicate={(node.Predicate is null ? "" : GraphProjectionText.Describe(node.Predicate))}"); }
            }
            if (shape.Edge is { } edge && (edge.LabelExpression is not null || edge.Type is not null || edge.Properties.Count != 0 || edge.Predicate is not null))
            { Add("ElementPrefilter", parent, edge.Variable, detail: $"scope={scope}; type={edge.LabelExpression?.ToString() ?? edge.Type}; properties={string.Join(",", edge.Properties.Keys)}; predicate={(edge.Predicate is null ? "" : GraphProjectionText.Describe(edge.Predicate))}"); }
            for (int i = 0; i < shape.Segments.Count; i++)
            { DescribePrefilters(shape.Segments[i].Body, parent, $"{scope}/{i}{(shape.Segments[i].Quantifier is null ? "" : "/iteration")}"); }
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
        GqlMergeStage => "Merge",
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
        GqlExpression expression => GraphProjectionText.Describe(expression),
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };
}
