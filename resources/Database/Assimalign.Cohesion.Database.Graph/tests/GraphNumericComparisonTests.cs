using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphNumericComparisonTests
{
    private static readonly (object? Left, object? Right, int? Order)[] _pairs =
    [
        (0.1m, 0.1d, -1), (0.1d, 0.1m, 1), (1m, 1d, 0), (1d, 1m, 0), (1L, 1d, 0),
        (9007199254740993L, 9007199254740992d, 1), (9007199254740992d, 9007199254740993L, -1),
        (9007199254740993L, 9007199254740994d, -1), (9007199254740994d, 9007199254740993L, 1),
        (long.MaxValue, (double)long.MaxValue, -1), (long.MinValue, (double)long.MinValue, 0),
        (ulong.MaxValue, (double)ulong.MaxValue, -1), (decimal.MaxValue, (double)decimal.MaxValue, -1),
        (double.Epsilon, 0m, 1), (0m, double.Epsilon, -1), (float.Epsilon, (double)float.Epsilon, 0),
        (0d, -0d, 0), (-0f, 0m, 0), (null, null, null), (null, 0d, null), (0d, null, null),
        (double.NaN, double.NaN, 0), (float.NaN, double.NaN, 0), (double.NaN, float.NaN, 0),
        (double.NaN, BitConverter.Int64BitsToDouble(0x7ff0_0000_0000_0001), 0),
        (double.NaN, 0m, -1), (0m, double.NaN, 1),
        (float.PositiveInfinity, double.PositiveInfinity, 0), (double.NegativeInfinity, decimal.MinValue, -1),
    ];

    [Fact(DisplayName = "Cohesion Test [Graph] - Numeric comparison: equality, ordering, IN and DISTINCT share exact values and hashes")]
    public void Evaluate_NumericPairs_ShouldAgreeWithDistinctAndHashing()
    {
        // Arrange
        var bindings = new Dictionary<string, object?>();
        var comparer = GraphValueComparer.Instance;

        foreach (var (left, right, order) in _pairs)
        {
            var a = new GqlLiteralExpression(left);
            var b = new GqlLiteralExpression(right);
            bool nullOperand = left is null || right is null;
            bool same = nullOperand ? left is null && right is null : order == 0;

            // Act / Assert
            foreach (string op in new[] { "=", "<>", "!=", "<", "<=", ">", ">=" })
            {
                object? expected = nullOperand ? null : op switch
                {
                    "=" => same, "<>" or "!=" => !same, "<" => order < 0,
                    "<=" => order <= 0, ">" => order > 0, _ => order >= 0,
                };
                GraphExpressionEvaluator.Evaluate(new GqlBinaryExpression(a, op, b), bindings).ShouldBe(expected);
            }
            GraphExpressionEvaluator.Evaluate(new GqlInExpression(a, new GqlListExpression([b])), bindings)
                .ShouldBe(nullOperand ? null : (object)same);
            comparer.Equals(left, right).ShouldBe(same);
            comparer.Equals(right, left).ShouldBe(same);
            new HashSet<object?[]>(GraphRowComparer.Instance) { new[] { left }, new[] { right } }.Count.ShouldBe(same ? 1 : 2);
            if (same) { comparer.GetHashCode(left).ShouldBe(comparer.GetHashCode(right)); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Numeric comparison: live equality and DISTINCT agree before and after indexing")]
    public async Task Execute_NumericPairs_ShouldAgreeWithDistinctAndIndexedFilteringAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("numeric-agreement", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (var (left, right, _) in _pairs)
        {
            if (!Finite(left) || !Finite(right)) { continue; }
            var parameters = new Dictionary<string, object?> { ["a"] = left, ["b"] = right };
            await session.ExecuteAsync("INSERT (:Value {v: $a}), (:Value {v: $b})", parameters, CancellationToken.None);

            // Act / Assert
            await using (var result = (await session.ExecuteAsync("RETURN $a = $b AS equal, $a IN [$b] AS member", parameters, CancellationToken.None))
                .ShouldBeAssignableTo<DatabaseResultSet>())
            {
                await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                {
                    object? expected = left is null || right is null ? null : GraphValueComparer.Instance.Equals(left, right);
                    row.GetValue(0).ShouldBe(expected);
                    row.GetValue(1).ShouldBe(expected);
                }
            }
            (await CountAsync("MATCH (n:Value) RETURN DISTINCT n.v", parameters))
                .ShouldBe(GraphValueComparer.Instance.Equals(left, right) ? 1 : 2);
            for (int indexed = 0; indexed < 2; indexed++)
            {
                if (indexed == 1)
                { await GraphSchema.Open(database, session).CreateIndexAsync("Value", "by_value", "v", CancellationToken.None); }
                (await CountAsync("MATCH (n:Value) WHERE n.v = $a RETURN DISTINCT n.v", parameters)).ShouldBe(left is null ? 0 : 1);
            }
            await session.ExecuteAsync("MATCH (n:Value) DELETE n", cancellationToken: CancellationToken.None);
            await GraphSchema.Open(database, session).DropLabelAsync("Value", CancellationToken.None);
        }

        async Task<int> CountAsync(string source, IReadOnlyDictionary<string, object?> parameters)
        {
            await using var result = (await session.ExecuteAsync(source, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
            return count;
        }

        static bool Finite(object? value) => value switch
        { double number => double.IsFinite(number), float number => float.IsFinite(number), _ => true };
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Numeric comparison: NaN is rejected consistently before equality or DISTINCT execution")]
    [InlineData("RETURN $v = $v AS equal")]
    [InlineData("RETURN DISTINCT $v")]
    public async Task Execute_NaN_ShouldRejectDuringPlanningAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("nan-agreement", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source,
            new Dictionary<string, object?> { ["v"] = double.NaN }, CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
    }
}
