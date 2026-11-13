using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphEnumAdmissionTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Allocation-free enum admission sets cover every declared member (#1808)")]
    public void Plan_Fix4EnumAdmission_ShouldMatchDeclaredValues()
    {
        // Arrange / Act / Assert: keep these eleven accepted sets paired with the
        // planner, path-shape and incident-edge guards. New members require review.
        Pin<GqlSetQuantifier>([GqlSetQuantifier.All, GqlSetQuantifier.Distinct]);
        Pin<GqlSortDirection>([GqlSortDirection.Ascending, GqlSortDirection.Descending]);
        Pin<GqlNullOrdering>([GqlNullOrdering.Default, GqlNullOrdering.First, GqlNullOrdering.Last]);
        Pin<GqlCastType>([GqlCastType.Int32, GqlCastType.Int64, GqlCastType.Float32, GqlCastType.Float64,
            GqlCastType.Decimal, GqlCastType.String, GqlCastType.Boolean, GqlCastType.Date, GqlCastType.Timestamp]);
        Pin<GqlAggregateFunction>([GqlAggregateFunction.Count, GqlAggregateFunction.Sum, GqlAggregateFunction.Avg,
            GqlAggregateFunction.Min, GqlAggregateFunction.Max, GqlAggregateFunction.CollectList]);
        Pin<GqlLogicalOperator>([GqlLogicalOperator.And, GqlLogicalOperator.Or, GqlLogicalOperator.Xor]);
        Pin<GqlStringPredicateOperator>([GqlStringPredicateOperator.StartsWith, GqlStringPredicateOperator.EndsWith, GqlStringPredicateOperator.Contains]);
        Pin<GqlPatternDirection>([GqlPatternDirection.Outgoing, GqlPatternDirection.Incoming, GqlPatternDirection.Undirected, GqlPatternDirection.LeftOrRight]);
        Pin<GqlCompositeOperator>([GqlCompositeOperator.Union, GqlCompositeOperator.UnionAll, GqlCompositeOperator.Except,
            GqlCompositeOperator.ExceptAll, GqlCompositeOperator.Intersect, GqlCompositeOperator.IntersectAll, GqlCompositeOperator.Otherwise]);
        Pin<GqlPathSelectorKind>([GqlPathSelectorKind.Any, GqlPathSelectorKind.All, GqlPathSelectorKind.AnyShortest,
            GqlPathSelectorKind.AllShortest, GqlPathSelectorKind.Shortest, GqlPathSelectorKind.ShortestGroups]);
        Pin<GraphIncidentDirection>([GraphIncidentDirection.Both, GraphIncidentDirection.Outgoing, GraphIncidentDirection.Incoming]);

        // Pin the production expressions too: changing a guard without changing
        // its declared accepted set must fail just as adding an enum member does.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "resources", "Database"))) { root = root.Parent; }
        root.ShouldNotBeNull();
        string source = Path.Combine(root.FullName, "resources", "Database", "Assimalign.Cohesion.Database.Graph", "src");
        foreach (var (file, expression, count) in new (string, string, int)[]
        {
            ("Internal/Planning/GraphPlanner.cs", "query.SetQuantifier is GqlSetQuantifier.All or GqlSetQuantifier.Distinct", 1),
            ("Internal/Planning/GraphPlanner.cs", "item.Direction is GqlSortDirection.Ascending or GqlSortDirection.Descending", 1),
            ("Internal/Planning/GraphPlanner.cs", "item.NullOrdering is GqlNullOrdering.Default or GqlNullOrdering.First or GqlNullOrdering.Last", 1),
            ("Internal/Planning/GraphPlanner.cs", "cast.Target is >= GqlCastType.Int32 and <= GqlCastType.Timestamp", 1),
            ("Internal/Planning/GraphPlanner.cs", "aggregate.Function is >= GqlAggregateFunction.Count and <= GqlAggregateFunction.CollectList", 1),
            ("Internal/Planning/GraphPlanner.cs", "logical.Operator is GqlLogicalOperator.And or GqlLogicalOperator.Or or GqlLogicalOperator.Xor", 1),
            ("Internal/Planning/GraphPlanner.cs", "predicate.Operator is GqlStringPredicateOperator.StartsWith or GqlStringPredicateOperator.EndsWith or GqlStringPredicateOperator.Contains", 1),
            ("Internal/Planning/GraphPlanner.cs", "relationship.Direction is >= GqlPatternDirection.Outgoing and <= GqlPatternDirection.LeftOrRight", 1),
            ("Internal/Planning/GraphPlanner.Composite.cs", "composite.Operator is >= GqlCompositeOperator.Union and <= GqlCompositeOperator.Otherwise", 2),
            ("Internal/Planning/GraphPathShape.cs", "selector.Kind is >= GqlPathSelectorKind.Any and <= GqlPathSelectorKind.ShortestGroups", 1),
            ("Storage/GraphStore.cs", "direction is GraphIncidentDirection.Both or GraphIncidentDirection.Outgoing or GraphIncidentDirection.Incoming", 1),
        })
        {
            File.ReadAllText(Path.Combine(source, file)).Split(expression, StringSplitOptions.None).Length.ShouldBe(count + 1);
        }
    }

    private static void Pin<T>(T[] accepted) where T : struct, Enum => accepted.ShouldBe(Enum.GetValues<T>());
}
