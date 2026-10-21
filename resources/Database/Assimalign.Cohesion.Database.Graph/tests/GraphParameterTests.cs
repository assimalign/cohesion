using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphParameterTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Parameters: unbound execution and EXPLAIN abort an explicit transaction until rollback")]
    [InlineData("INSERT (:P {k: $missing})")]
    [InlineData("INSERT (:P {k: @missing})")]
    [InlineData("EXPLAIN INSERT (:P {k: $missing})")]
    [InlineData("EXPLAIN INSERT (:P {k: @missing})")]
    public async Task Execute_UnboundParameterInTransaction_ShouldAbortUntilRollback(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Earlier)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        var refused = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT (:Later)", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        failure.Message.ShouldContain("missing", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.Faulted);
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        refused.Diagnostics[0].Code.ShouldBe("COHDBG007");
        refused.Message.ShouldContain(failure.Message, Case.Sensitive);
        refused.InnerException.ShouldBeSameAs(failure);
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        (await RowsAsync(observer, "MATCH (n) RETURN n")).ShouldBeEmpty();
        (await RowsAsync(observer, "SHOW LABELS")).ShouldBeEmpty();

        await transaction.RollbackAsync(CancellationToken.None);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await session.ExecuteAsync("INSERT (:After)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await RowsAsync(observer, "SHOW LABELS")).ShouldHaveSingleItem()[2].ShouldBe("After");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Parameters: missing bindings fail before waiting for another writer")]
    public async Task Execute_UnboundWrite_ShouldFailBeforeWriterLock()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", cancellation.Token);
        await using var writer = await database.CreateSessionAsync(cancellation.Token);
        await using var reader = await database.CreateSessionAsync(cancellation.Token);
        await using var transaction = await writer.BeginTransactionAsync(cancellation.Token);
        await writer.ExecuteAsync("INSERT (:Held)", cancellationToken: cancellation.Token);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await reader.ExecuteAsync("INSERT (:P {k: $missing})", cancellationToken: cancellation.Token));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        await transaction.RollbackAsync(cancellation.Token);
        (await RowsAsync(reader, "SHOW LABELS")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Parameters: node and relationship property maps bind on insertion and matching")]
    [InlineData("INSERT")]
    [InlineData("CREATE")]
    public async Task Execute_RelationshipParameters_ShouldBindOnBothMapPaths(string verb)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["name"] = "Ada", ["weight"] = 2.5m };

        // Act
        (await session.ExecuteAsync($"{verb} (:P {{name: $name}})-[:T {{weight: @weight}}]->(:P)",
            parameters, CancellationToken.None)).AffectedCount.ShouldBe(3);
        var rows = await RowsAsync(session,
            "MATCH (a:P {name: @name})-[r:T {weight: $weight}]->(b) WHERE $weight = r.weight RETURN a.name, r.weight", parameters);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { "Ada", 2.5m });
        rows[0][1].ShouldBeOfType<decimal>();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Parameters: binding failures precede all writes, even on empty or skipped branches")]
    [InlineData("INSERT (:P {k: $missing})")]
    [InlineData("EXPLAIN INSERT (:P {k: @missing})")]
    [InlineData("MATCH (a:Missing) WHERE TRUE OR $missing RETURN a")]
    [InlineData("MATCH (a:Missing) WHERE FALSE AND $missing RETURN a")]
    public async Task Execute_UnboundParameter_ShouldFailBeforeWriting(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        error.Message.ShouldContain("missing", Case.Sensitive);
        (await RowsAsync(session, "MATCH (n) RETURN n")).ShouldBeEmpty();
        (await RowsAsync(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Parameters: bare names and both sigils share ordinal lookup")]
    [InlineData("name")]
    [InlineData("$name")]
    [InlineData("@name")]
    public async Task Execute_NameSpelling_ShouldUseSharedContract(string key)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        { [key] = "Ada", ["unused"] = new object() };

        // Act
        (await session.ExecuteAsync("INSERT (:P {k: $name}), (:P {k: @name})", parameters, CancellationToken.None)).AffectedCount.ShouldBe(2);

        // Assert
        (await RowsAsync(session, "MATCH (n:P {k: $name}) RETURN n.k", parameters)).Select(row => row[0]).ShouldBe(["Ada", "Ada"]);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT (:P {k: $name})", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "wrong" }, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        (await session.ExecuteAsync("INSERT (:P {k: $1})", new Dictionary<string, object?> { ["1"] = 1 }, CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Parameters: normalized collisions fail even for unused entries")]
    [InlineData("name", "$name")]
    [InlineData("@name", "$name")]
    public async Task Execute_CollidingKeys_ShouldReportGraphCode(string first, string second)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        foreach (string source in new[] { "INSERT (:P)", "EXPLAIN INSERT (:P)", "SHOW LABELS" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                source, new Dictionary<string, object?> { [first] = 1, [second] = 2 }, CancellationToken.None));

            // Assert
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG014");
            error.Message.ShouldContain("name", Case.Sensitive);
        }
        (await RowsAsync(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    public static IEnumerable<object?[]> ScalarCases()
    {
        yield return [null, "NULL"];
        yield return [42L, "42"];
        yield return [42, "42"];
        yield return [2.5, "2.5"];
        yield return [true, "TRUE"];
        yield return ["Ada", "'Ada'"];
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Parameters: scalar predicates and insert counts equal their literal twins")]
    [MemberData(nameof(ScalarCases))]
    public async Task Execute_ScalarParameter_ShouldMatchLiteral(object? value, string literal)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["p"] = value };
        await session.ExecuteAsync($"INSERT (:P {{x: {literal}}}), (:P)", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string template in new[] { "MATCH (a:P {x: VALUE}) RETURN a", "MATCH (a:P) WHERE a.x = VALUE RETURN a" })
        {
            var inline = await RowsAsync(session, template.Replace("VALUE", literal, StringComparison.Ordinal));
            var bound = await RowsAsync(session, template.Replace("VALUE", "$p", StringComparison.Ordinal), parameters);
            bound.Select(row => ((GraphNode)row[0]!).Id).ShouldBe(inline.Select(row => ((GraphNode)row[0]!).Id));
            bound.Count.ShouldBe(value is null && template.Contains("WHERE", StringComparison.Ordinal) ? 0 : 1);
        }
        (await session.ExecuteAsync("CREATE (:Bound {x: @p})", parameters, CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await session.ExecuteAsync($"CREATE (:Inline {{x: {literal}}})", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        var stored = (await RowsAsync(session, "MATCH (a:Bound) RETURN a.x")).Single()[0];
        stored.ShouldBe(value);
        stored?.GetType().ShouldBe(value?.GetType());
    }

    public static IEnumerable<object[]> InvalidValues()
    {
        foreach (object value in new object[] { Guid.Empty, DateTime.UnixEpoch, DateOnly.MinValue, TimeSpan.Zero,
            Array.Empty<byte>(), new List<int> { 1 }, new Dictionary<string, object?> { ["x"] = 1 }, double.NaN, float.PositiveInfinity })
        { yield return [value]; }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Parameters: unsupported leaves fail and composites compare unequal without index seeks")]
    [MemberData(nameof(InvalidValues))]
    public async Task Execute_ParameterValue_ShouldEnforcePositionRules(object value)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person {name: 'Ada'})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("Person", "by_name", "name", CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["p"] = value };

        // Act / Assert
        foreach (string source in new[] { "INSERT (:P {k: $p})", "INSERT (:P)-[:T {k: $p}]->(:P)",
            "MATCH (a) WHERE a.k = $p RETURN a", "MATCH (a:Person {name: $p}) RETURN a",
            "MATCH (a:Person) WHERE a.name = $p RETURN a", "MATCH (a:Missing) WHERE TRUE OR $p RETURN a" })
        {
            if (value is System.Collections.IList and not byte[] or IReadOnlyDictionary<string, object?> && source.Contains("WHERE", StringComparison.Ordinal))
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, parameters, CancellationToken.None);
                var rows = new List<DatabaseRow>();
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
                rows.ShouldBeEmpty();
                continue;
            }
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, parameters, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
        }
        (await RowsAsync(session, "MATCH (a) RETURN a")).Count.ShouldBe(1);
        (await RowsAsync(session, "SHOW LABELS")).Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Parameters: every execution selects its own nonnull anchor")]
    [InlineData("MATCH (a:Person {name: $name}) RETURN a.name")]
    [InlineData("MATCH (a:Person) WHERE a.name = $name RETURN a.name")]
    [InlineData("MATCH (a:Person) WHERE $name = a.name RETURN a.name")]
    public async Task Plan_ParameterEquality_ShouldBindAnchorPerExecution(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person {name: 'Ada'}), (:Person {name: 'Bob'})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("Person", "by_name", "name", CancellationToken.None);
        var statement = GraphQueryRequest.FromGql(source).Statement;

        // Act / Assert
        foreach (string? name in new string?[] { "Ada", "Bob", null })
        {
            var parameters = new Dictionary<string, object?> { ["name"] = name };
            var plan = await database.RunAsync(session, operation => ValueTask.FromResult(
                new GraphPlanner(database, operation.Context.Snapshot).Plan(statement.GqlExpression, parameters)), CancellationToken.None);
            plan.MatchClauses.Single().Paths.Single().Anchor.Property.ShouldBe(name is null ? null : "name");
            plan.MatchClauses.Single().Paths.Single().Anchor.Value.ShouldBe(name);
            var rows = await RowsAsync(session, source, parameters);
            rows.Count.ShouldBe(name is null ? 0 : 1);
            if (name is not null) { rows[0][0].ShouldBe(name); }
            var description = await RowsAsync(session, "EXPLAIN " + source, parameters);
            description.ShouldContain(row => Equals(row[2], name == null ? "LabelScan" : "IndexSeek"));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Parameters: decimal and Int32 schema keys preserve runtime types")]
    public async Task Execute_TypedParameter_ShouldPreserveSchemaAndReadBackTypes()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Typed");
        await schema.SaveLabelAsync(label, CancellationToken.None);
        await schema.SavePropertyKeyAsync(new(label.Id, "k", DatabaseType.Int32, true), CancellationToken.None);

        // Act
        await session.ExecuteAsync("INSERT (:Typed {k: $int}), (:N {k: $decimal}), (:N {k: 42})",
            new Dictionary<string, object?> { ["int"] = 42, ["decimal"] = 42m }, CancellationToken.None);

        // Assert
        (await RowsAsync(session, "MATCH (a:Typed) RETURN a.k")).Single()[0].ShouldBeOfType<int>().ShouldBe(42);
        (await RowsAsync(session, "MATCH (a:N) WHERE a.k = $p RETURN a.k", new Dictionary<string, object?> { ["p"] = 42m }))
            .Select(row => row[0]).ShouldBe(new object?[] { 42m, 42L }, ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Parameters: EXPLAIN binds values and leaves mutation metadata unchanged")]
    public async Task Execute_ExplainWithParameters_ShouldBindWithoutWriting()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        (await RowsAsync(session, "EXPLAIN INSERT (:P {k: $p})", new Dictionary<string, object?> { ["p"] = 42 })).ShouldNotBeEmpty();

        // Assert
        (await RowsAsync(session, "SHOW LABELS")).ShouldBeEmpty();
        (await RowsAsync(session, "MATCH (n) RETURN n")).ShouldBeEmpty();
    }

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string source,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, parameters, CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
