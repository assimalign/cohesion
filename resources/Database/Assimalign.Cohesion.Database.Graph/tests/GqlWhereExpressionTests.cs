using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlWhereExpressionTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - WHERE: all logical truth-table combinations use three-valued logic")]
    public void Evaluate_TruthTables_ShouldPreserveUnknown()
    {
        // Arrange
        bool?[] values = [false, true, null];
        bool?[,] and = { { false, false, false }, { false, true, null }, { false, null, null } };
        bool?[,] or = { { false, true, null }, { true, true, true }, { null, true, null } };
        bool?[,] xor = { { false, true, null }, { true, false, null }, { null, null, null } };
        var bindings = new Dictionary<string, object?>();

        // Act / Assert
        for (int i = 0; i < values.Length; i++)
        {
            var left = new GqlLiteralExpression(values[i]);
            GraphExpressionEvaluator.Evaluate(new GqlUnaryExpression("NOT", left), bindings).ShouldBe(!values[i]);
            for (int j = 0; j < values.Length; j++)
            {
                var right = new GqlLiteralExpression(values[j]);
                foreach (var (op, table) in new[] { (GqlLogicalOperator.And, and), (GqlLogicalOperator.Or, or), (GqlLogicalOperator.Xor, xor) })
                {
                    GraphExpressionEvaluator.Evaluate(new GqlLogicalExpression(op, [left, right]), bindings).ShouldBe(table[i, j]);
                }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - WHERE: arithmetic promotes the typed API numeric types")]
    public async Task Execute_TypedNumericProperties_ShouldUseRecordedPromotionAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var database = await engine.CreateDatabaseAsync("numeric", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await database.CreateNodeAsync(session, ["N"], new Dictionary<string, object?>
        {
            ["d"] = 1.25m, ["u"] = ulong.MaxValue, ["f"] = 1.5f, ["i"] = 42, ["ui"] = uint.MaxValue,
        }, CancellationToken.None);

        // Act
        var rows = await ValuesAsync(session, "MATCH (a:N) WHERE a.d + 1 = 2.25 AND a.u - 1 > 9223372036854775807 AND a.f + 1 = 2.5 AND a.i / 5 = 8 AND a.ui + 1 = 4294967296 RETURN a.i");

        // Assert
        rows.ShouldBe([42]);
        object[] integrals = [(sbyte)1, (byte)1, (short)1, (ushort)1, 1, 1u, 1L];
        foreach (object value in integrals)
        {
            Arithmetic(value, 1L).ShouldBeOfType<long>().ShouldBe(2L);
        }
        Arithmetic(1UL, 1L).ShouldBeOfType<decimal>().ShouldBe(2m);
        Arithmetic(1.25m, 1L).ShouldBeOfType<decimal>().ShouldBe(2.25m);
        Arithmetic(1.25m, 1f).ShouldBeOfType<double>().ShouldBe(2.25d);
        Arithmetic(1UL, 1d).ShouldBeOfType<double>().ShouldBe(2d);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - WHERE: value errors are coded and the session remains usable")]
    [InlineData("a.age / 0")]
    [InlineData("a.age % 0")]
    [InlineData("a.age / 0.0")]
    [InlineData("a.age % 0.0")]
    [InlineData("9223372036854775807 + a.age")]
    [InlineData("'a' + 1")]
    [InlineData("a.age")]
    [InlineData("NOT 1")]
    [InlineData("1 IS TRUE")]
    [InlineData("TRUE AND 1")]
    [InlineData("'a' || 1")]
    [InlineData("-(-9223372036854775808)")]
    [InlineData("1e308 * 1e308")]
    [InlineData("a.age IN [42, 1 / 0]")]
    [InlineData("a.age IN [1 / 0, 42]")]
    [InlineData("1 IN [1, 'a' + 1]")]
    [InlineData("1 IN ['a' + 1, 1]")]
    [InlineData("NULL IN [1 / 0]")]
    [InlineData("-9223372036854775808 / -1 = 0")]
    public async Task Execute_ValueError_ShouldCodeAndRecoverAsync(string predicate)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var database = await engine.CreateDatabaseAsync("fault", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person {age: 42})", cancellationToken: CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("MATCH (a:Person) WHERE " + predicate + " RETURN a.age", cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
        error.Message.ShouldNotContain("COHDBG013", Case.Sensitive);
        (await ValuesAsync(session, "MATCH (a:Person) RETURN a.age")).ShouldBe([42L]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - WHERE: only AND conjuncts can supply an index anchor")]
    [InlineData("a.name = 'Alice' OR a.age = 17", null, "Alice,Bob")]
    [InlineData("NOT (a.name = 'Alice')", null, "Bob")]
    [InlineData("a.name = 'Alice' AND (a.age = 1 OR a.age = 42)", "name", "Alice")]
    public async Task Plan_BooleanExpression_ShouldAnchorOnlyConjunctsAsync(string predicate, string? property, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var database = await engine.CreateDatabaseAsync("anchors", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person {name: 'Alice', age: 42}), (:Person {name: 'Bob', age: 17})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("Person", "by_name", "name", CancellationToken.None);
        string query = "MATCH (a:Person) WHERE " + predicate + " RETURN a.name";

        // Act
        var plan = await database.RunAsync(session, operation => new ValueTask<GraphPlan>(
            new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(query).Statement.GqlExpression)), CancellationToken.None);
        var rows = await ValuesAsync(session, query);

        // Assert
        plan.MatchClauses.Single().Paths.Single().Anchor.Property.ShouldBe(property);
        rows.ShouldBe(expected.Split(',').Cast<object?>());
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - WHERE: every recursive node keeps the evaluator stack guard")]
    [InlineData("NOT ")]
    [InlineData("- ")]
    [InlineData("1 IN [")]
    [InlineData("IS NULL")]
    [InlineData("IS TRUE")]
    [InlineData("CONTAINS")]
    [InlineData("+")]
    public void Evaluate_RecursiveNode_ShouldCheckStack(string form)
    {
        // Arrange
        GqlExpression expression = new GqlLiteralExpression(true);
        for (int i = 0; i < 100_000; i++)
        {
            expression = form switch
            {
                "NOT " => new GqlUnaryExpression("NOT", expression),
                "- " => new GqlUnaryExpression("-", expression),
                "IS NULL" => new GqlIsNullExpression(expression, false),
                "IS TRUE" => new GqlTruthTestExpression(expression, true, false),
                "CONTAINS" => new GqlStringPredicateExpression(expression, GqlStringPredicateOperator.Contains, new GqlLiteralExpression("x")),
                "+" => new GqlBinaryExpression(expression, "+", new GqlLiteralExpression(1L)),
                _ => new GqlInExpression(new GqlLiteralExpression(1L), new GqlListExpression([expression])),
            };
        }

        // Act / Assert
        Should.Throw<InsufficientExecutionStackException>(() => GraphExpressionEvaluator.Evaluate(expression, new Dictionary<string, object?>()));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - WHERE: malformed public expression nodes fail shape validation")]
    public async Task Plan_MalformedScalarNodes_ShouldRejectEveryChildAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var database = await engine.CreateDatabaseAsync("shape", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var literal = new GqlLiteralExpression(true);
        GqlExpression[] invalid =
        [
            new GqlUnaryExpression("invalid", literal),
            new GqlUnaryExpression("NOT", null!),
            new GqlIsNullExpression(null!, false),
            new GqlTruthTestExpression(null!, true, false),
            new GqlListExpression(null!),
            new GqlMapExpression([new KeyValuePair<string, GqlExpression>("k", null!)]),
            new GqlMapExpression([new KeyValuePair<string, GqlExpression>("k", literal), new KeyValuePair<string, GqlExpression>("k", literal)]),
            new GqlCaseExpression(null, [], null),
            new GqlCaseExpression(null, [new GqlCaseAlternative(literal, null!)], null),
            new GqlCoalesceExpression([literal]),
            new GqlNullIfExpression(literal, null!),
            new GqlSubscriptExpression(null!, literal),
            new GqlMemberExpression(null!, "k"),
            new GqlInExpression(literal, null!),
            new GqlInExpression(literal, new GqlListExpression(null!)),
            new GqlInExpression(literal, new GqlListExpression([null!])),
            new GqlStringPredicateExpression(literal, (GqlStringPredicateOperator)99, literal),
            new GqlStringPredicateExpression(null!, GqlStringPredicateOperator.Contains, literal),
            new GqlLogicalExpression(GqlLogicalOperator.Or, [literal, null!]),
        ];

        // Act / Assert
        foreach (var predicate in invalid)
        {
            var query = new GqlQueryExpression(
                [new GqlPathPattern([new GqlNodePattern("a", [], new Dictionary<string, GqlExpression>())], [])],
                predicate, [], [], false, [new GqlProjection(new GqlPropertyExpression("a", "x"))]);
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - WHERE: integral remainder by minus one is representable zero")]
    [InlineData(long.MinValue)]
    [InlineData(-42L)]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(long.MaxValue)]
    public void Evaluate_RemainderByMinusOne_ShouldReturnInt64Zero(long dividend)
    {
        // Arrange
        var expression = new GqlBinaryExpression(new GqlLiteralExpression(dividend), "%", new GqlLiteralExpression(-1L));

        // Act
        var result = GraphExpressionEvaluator.Evaluate(expression, new Dictionary<string, object?>());

        // Assert
        result.ShouldBeOfType<long>().ShouldBe(0L);
    }

    private static object? Arithmetic(object left, object right) => GraphExpressionEvaluator.Evaluate(
        new GqlBinaryExpression(new GqlLiteralExpression(left), "+", new GqlLiteralExpression(right)), new Dictionary<string, object?>());

    private static async Task<List<object?>> ValuesAsync(GraphDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        List<object?> values = [];
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        return values;
    }
}
