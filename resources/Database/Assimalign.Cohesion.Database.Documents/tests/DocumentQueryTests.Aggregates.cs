using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates: DISTINCT uses grouping equality and ALL preserves duplicate inputs")]
    public async Task Execute_AggregateQuantifiers_ShouldDeduplicateNumbersAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("quantifiers", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:1.0}),('c',{v:3}),('d',{v:NULL}),('e',{})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "SELECT COUNT(DISTINCT v) AS c, SUM(DISTINCT v) AS s, AVG(DISTINCT v) AS a, MIN(DISTINCT v) AS lo, MAX(DISTINCT v) AS hi, COUNT(ALL v) AS ca, SUM(ALL v) AS sa, AVG(ALL v) AS aa, MIN(ALL v) AS la, MAX(ALL v) AS ha, COUNT(*) AS star FROM items")).Single();
        // Assert
        Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ShouldBe([2L,4m,2m,1m,3m,3L,5m,5m/3m,1m,3m,5L]);
        var grouped = await RowsAsync(session, "SELECT v IS VALUED AS k, COUNT(DISTINCT v) AS n FROM items GROUP BY v IS VALUED HAVING COUNT(DISTINCT v) > 0 ORDER BY SUM(DISTINCT v)");
        grouped.ShouldHaveSingleItem().GetValue(1).ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates: arrays retain NULL and use structural DISTINCT without promising order")]
    public async Task Execute_ArrayAggregate_ShouldRetainOwnedStructuralValuesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("array-aggregate", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:[1,NULL]}),('b',{v:[1.0,NULL]}),('c',{v:{a:NULL,b:1}}),('d',{v:{b:1.0,a:NULL}}),('e',{v:NULL}),('f',{}),('g',{v:TRUE}),('h',{v:'text'})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "SELECT ARRAY_AGG(v) AS a, ARRAY_AGG(DISTINCT v) AS d, COUNT(DISTINCT v) AS n FROM items")).Single();
        // Assert
        row.GetValue(0).ShouldBeOfType<JsonElement>().GetArrayLength().ShouldBe(7);
        var distinct = row.GetValue(1).ShouldBeOfType<JsonElement>();
        distinct.GetArrayLength().ShouldBe(5);
        distinct.EnumerateArray().Count(x => x.ValueKind == JsonValueKind.Null).ShouldBe(1);
        distinct.EnumerateArray().Count(x => x.ValueKind == JsonValueKind.Array).ShouldBe(1);
        distinct.EnumerateArray().Count(x => x.ValueKind == JsonValueKind.Object).ShouldBe(1);
        row.GetValue(2).ShouldBe(4L);
        await session.ExecuteAsync("DELETE FROM items", cancellationToken: CancellationToken.None);
        distinct.EnumerateArray().Single(x => x.ValueKind == JsonValueKind.Object).GetProperty("a").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Aggregates: empty and wrong-kind inputs use each native aggregate's NULL rule")]
    [InlineData("WHERE FALSE")]
    [InlineData("WHERE TRUE")]
    public async Task Execute_NewAggregateEmptyInputs_ShouldReturnNullAsync(string filter)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("empty-aggregates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{}),('b',{v:NULL}),('c',{v:'x'}),('d',{v:[]}),('e',{v:{}})", cancellationToken: CancellationToken.None);
        // Act
        var result = (await session.ExecuteAsync("SELECT ARRAY_AGG(absent) AS a, BOOL_AND(v) AS b, BOOL_OR(v) AS c, EVERY(v) AS d, STDDEV(v) AS s, VARIANCE(v) AS v FROM items " + filter, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        // Assert
        await using (result)
        {
            result.Columns.Select(x => x.Type).ShouldBe([DatabaseType.Json,DatabaseType.Boolean,DatabaseType.Boolean,DatabaseType.Boolean,DatabaseType.Decimal,DatabaseType.Decimal]);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { Enumerable.Range(0,6).All(row.IsNull).ShouldBeTrue(); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates: Boolean inputs are strict and aggregate EVERY accepts a quantified predicate")]
    public async Task Execute_BooleanAggregate_ShouldIgnoreUnknownAndNonBooleanInputsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("boolean-aggregate", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:TRUE,tags:[TRUE]}),('b',{v:FALSE,tags:[]}),('c',{v:NULL}),('d',{}),('e',{v:1}),('f',{v:'x'})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "SELECT BOOL_AND(v) AS a, BOOL_OR(v) AS o, EVERY(DISTINCT v) AS e, EVERY(EVERY x IN tags SATISFIES x END) AS q FROM items HAVING BOOL_OR(v)")).Single();
        // Assert
        Enumerable.Range(0,4).Select(row.GetValue).ShouldBe([false,true,false,true]);
        (await RowsAsync(session, "SELECT v IS VALUED AS k, EVERY(v) AS e FROM items GROUP BY v IS VALUED HAVING BOOL_OR(v)")).ShouldHaveSingleItem().GetValue(1).ShouldBe(false);
        (await RowsAsync(session, "SELECT BOOL_AND(1) AS a, BOOL_OR('x') AS o, EVERY({}) AS e FROM items")).Single().IsNull(0).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates: sample statistics skip nonnumeric inputs and center large offsets")]
    public async Task Execute_StatisticalAggregate_ShouldComputeSampleStatisticsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("statistics", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:2}),('c',{v:2.0}),('d',{v:3}),('e',{v:NULL}),('f',{}),('g',{v:'x'}),('h',{v:TRUE})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "SELECT VARIANCE(v) AS v, STDDEV(v) AS s, VARIANCE(DISTINCT v) AS dv, STDDEV(DISTINCT v) AS ds FROM items")).Single();
        // Assert
        row.GetValue(0).ShouldBeOfType<decimal>().ShouldBe(2m/3m, 0.000000000000000000000000001m);
        row.GetValue(1).ShouldBeOfType<decimal>().ShouldBe(0.816496580927726m, 0.000000000000001m);
        row.GetValue(2).ShouldBe(1m);
        row.GetValue(3).ShouldBe(1m);
        var singleton = (await RowsAsync(session, "SELECT STDDEV(v) AS s, VARIANCE(v) AS v FROM items WHERE v = 1 OR v IS NOT VALUED")).Single();
        singleton.GetValue(0).ShouldBe(0m); singleton.GetValue(1).ShouldBe(0m);
        await session.ExecuteAsync("UPDATE items SET v = CASE META().id WHEN 'a' THEN 10000000000000000000000000000 WHEN 'b' THEN 10000000000000000000000000001 WHEN 'c' THEN 10000000000000000000000000002 ELSE NULL END", cancellationToken: CancellationToken.None);
        var centered = (await RowsAsync(session, "SELECT STDDEV(v) AS s, VARIANCE(v) AS v FROM items")).Single();
        centered.GetValue(0).ShouldBe(1m); centered.GetValue(1).ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Aggregates: retention charges the statement budget and duplicate values do not consume set entries")]
    [InlineData("COUNT(DISTINCT v)")]
    [InlineData("ARRAY_AGG(v)")]
    [InlineData("ARRAY_AGG(DISTINCT v)")]
    public async Task Execute_AggregateBudget_ShouldRefuseAndReuseSessionAsync(string aggregate)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("aggregate-budget", new() { StatementMemoryBudget = 6144 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"v\":\"" + new string('x', 2000) + "\"}");
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, $"SELECT {aggregate} FROM items"));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        failure.Diagnostics[0].Start.ShouldBe(7);
        (await RowsAsync(session, "SELECT COUNT(*) FROM items")).Single().GetValue(0).ShouldBe(1L);
        await session.ExecuteAsync("UPDATE items SET v = 1", cancellationToken: CancellationToken.None);
        var duplicated = (await RowsAsync(session, "SELECT COUNT(DISTINCT v) AS c, ARRAY_AGG(DISTINCT v) AS a FROM items d UNNEST [1,1,1,1,1,1,1,1,1,1,1,1,1,1,1] x")).Single();
        duplicated.GetValue(0).ShouldBe(1L);
        duplicated.GetValue(1).ShouldBeOfType<JsonElement>().GetArrayLength().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates: EXPLAIN binds quantifiers without evaluating input expressions")]
    public async Task Execute_AggregateExplain_ShouldDescribeWithoutReadingAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("aggregate-explain", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        await using var result = (await session.ExecuteAsync("EXPLAIN SELECT ARRAY_AGG(DISTINCT 1/0) AS a, EVERY(TRUE) AS e, STDDEV(1) AS s FROM items HAVING COUNT(ALL 1) > 0", cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        // Assert
        var details = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { if (row.GetValue(5) is string detail) { details.Add(detail); } }
        details.ShouldContain("aggregates=ARRAY_AGG(DISTINCT), EVERY(ALL), STDDEV(ALL), COUNT(ALL)");
        session.LastQueryDocumentsRead.ShouldBe(0);
        await using var system = (await session.ExecuteAsync("EXPLAIN SELECT ARRAY_AGG(DISTINCT INDEX_NAME) FROM COHESION_SCHEMA.INDEXES",
            cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        await foreach (var row in system.GetRowsAsync(CancellationToken.None))
        { row.GetString(5).ShouldNotBeNull().ShouldContain("aggregates=ARRAY_AGG(DISTINCT)", Case.Sensitive); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Aggregates: invalid contexts and statistic overflow preserve coded failures")]
    [InlineData("SELECT STDDEV(SUM(v)) FROM items", "CD05001")]
    [InlineData("SELECT * FROM items WHERE EVERY(v)", "CD05001")]
    [InlineData("SELECT COUNT(*) FROM items GROUP BY ARRAY_AGG(v)", "CD05001")]
    [InlineData("SELECT EVERY(ANY x IN [1] SATISFIES EVERY(v) END) FROM items", "CD05001")]
    [InlineData("SELECT VARIANCE(v * 100000000000000000000) FROM items", "CD02007")]
    [InlineData("SELECT SUM(DISTINCT 'x') FROM items", "CD02008")]
    public async Task Execute_InvalidAggregate_ShouldKeepDiagnosticAsync(string text, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("invalid-aggregate", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:2})", cancellationToken: CancellationToken.None);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, text));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        failure.Diagnostics[0].Start.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Aggregates: direct ASTs cannot put aggregate quantifiers on scalar or star calls")]
    [InlineData("LOWER", false, OqlAggregateQuantifier.Distinct)]
    [InlineData("COUNT", true, OqlAggregateQuantifier.All)]
    [InlineData("COUNT", false, (OqlAggregateQuantifier)99)]
    public async Task Execute_DirectQuantifierAst_ShouldRefuseInvalidBindingAsync(string name, bool star, OqlAggregateQuantifier quantifier)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("direct-quantifier", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        var call = new OqlCallExpression(name, [star ? new OqlStarExpression() : new OqlLiteralExpression(1m)], quantifier: quantifier);
        var query = new OqlSelectExpression("items", null, [new OqlProjection(call, "n")], null, [], null, [], false, false);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(query)), CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01029");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Aggregates: exact retained-memory limit accepts equality and refuses one byte less")]
    [InlineData(575L, true)]
    [InlineData(576L, false)]
    public async Task Execute_DistinctBudgetBoundary_ShouldChargeExactlyAsync(long budget, bool fails)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("distinct-boundary", new() { StatementMemoryBudget = budget });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:2})", cancellationToken: CancellationToken.None);
        using var json = JsonDocument.Parse("[{\"v\":1},{\"v\":2}]");
        var group = json.RootElement.EnumerateArray().Select(value => new DocumentEvaluationContext(value)).ToArray();
        var call = new OqlCallExpression("COUNT", [new OqlPathExpression([new OqlPathSegment("v", null)])], quantifier: OqlAggregateQuantifier.Distinct);
        // Act / Assert: 256 accumulator + 128 set + two 96-byte scalar entries.
        await database.RunAsync(session, operation =>
        {
            var evaluator = new DocumentExpressionEvaluator(null, null, operation: operation);
            if (fails)
            {
                Should.Throw<DatabaseException>(() => evaluator.Evaluate(call, default, group)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            }
            else { evaluator.Evaluate(call, default, group).ShouldBe(2L); }
            evaluator.Memory.Bytes.ShouldBe(0);
            // Each one-value set is released before the next group.
            foreach (var document in group) { evaluator.Evaluate(call, default, [document]).ShouldBe(1L); evaluator.Memory.Bytes.ShouldBe(0); }
            return ValueTask.FromResult(true);
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Aggregates: array reservations accumulate across fresh groups")]
    [InlineData(3971L, true)]
    [InlineData(3972L, false)]
    public async Task Execute_GroupedArrayBudget_ShouldShareStatementCounterAsync(long budget, bool fails)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("array-boundary", new() { StatementMemoryBudget = budget });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:2})", cancellationToken: CancellationToken.None);
        // Act / Assert: source and groups reserve 2518; two output rows 1166; sort keys 288.
        const string query = "SELECT v, ARRAY_AGG(v) AS a FROM items GROUP BY v ORDER BY v";
        if (fails)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, query));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        }
        else
        {
            var rows = await RowsAsync(session, query);
            rows.Select(x => x.GetValue(1).ShouldBeOfType<JsonElement>().EnumerateArray().Single().GetDecimal()).ShouldBe([1m,2m]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Aggregates: argument-triggered cancellation stops the new accumulation loops")]
    [InlineData("ARRAY_AGG(cancel_after(v))")]
    [InlineData("ARRAY_AGG(DISTINCT cancel_after(v))")]
    [InlineData("COUNT(DISTINCT cancel_after(v))")]
    [InlineData("STDDEV(cancel_after(v))")]
    public async Task Execute_AggregateCancellation_ShouldStopAtRecordedInputAsync(string aggregate)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("cancel-aggregate");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("cancel_after", value =>
        { if (++calls == 3) { cancellation.Cancel(); } return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:2}),('c',{v:3}),('d',{v:4})", cancellationToken: CancellationToken.None);
        // Act
        var failure = await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync("SELECT " + aggregate + " FROM items", cancellationToken: cancellation.Token));
        // Assert
        failure.CancellationToken.ShouldBe(cancellation.Token);
        calls.ShouldBe(3);
        (await RowsAsync(session, "SELECT COUNT(*) FROM items")).Single().GetValue(0).ShouldBe(4L);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates: registered one-argument DISTINCT retains binding and unknown-input policy")]
    public async Task Execute_RegisteredDistinctAggregate_ShouldRespectCatalogAsync()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("registered-distinct");
        builder.Functions.Add(DocumentAggregateFunction.Create<long, DocumentValue, long>("admitted", () => 0L,
            (count, value) => count + 1, count => count, DocumentNullBehavior.CalledOnNullOrMissingInput));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:1.0}),('c',{v:NULL}),('d',{})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "SELECT admitted(DISTINCT v) AS d, admitted(ALL v) AS a FROM items")).Single();
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, "SELECT future(DISTINCT v) FROM items"));
        // Assert
        row.GetValue(0).ShouldBe(3L); row.GetValue(1).ShouldBe(4L);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00008");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Aggregates: projected DISTINCT ordering distinguishes input quantifiers")]
    [InlineData("COUNT(DISTINCT v)", "COUNT(ALL v)", true)]
    [InlineData("COUNT(ALL v)", "COUNT(v)", false)]
    public async Task Execute_QuantifiedProjectionOrdering_ShouldCompareAggregateSemanticsAsync(string projection, string ordering, bool fails)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("quantified-order", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:1.0})", cancellationToken: CancellationToken.None);
        string text = $"SELECT DISTINCT {projection} AS n FROM items ORDER BY {ordering}";
        // Act / Assert
        if (fails)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, text));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01014");
        }
        else { (await RowsAsync(session, text)).Single().GetValue(0).ShouldBe(2L); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates: exact moments preserve representable extreme-range sample statistics")]
    public async Task Execute_StatisticalRange_ShouldAvoidPrematureOverflowAndUnderflowAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("statistic-range", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{})", cancellationToken: CancellationToken.None);
        // Act
        var tiny = (await RowsAsync(session, "SELECT STDDEV(v) FROM items UNNEST [1e-20,2e-20] v")).Single();
        var near = (await RowsAsync(session, "SELECT STDDEV(v) FROM items UNNEST [0,1.6e-14] v")).Single();
        var wide = (await RowsAsync(session, "SELECT VARIANCE(v) AS v, STDDEV(v) AS s FROM items UNNEST [-2e14,2e14,0] v")).Single();
        var root = (await RowsAsync(session, "SELECT STDDEV(v) FROM items UNNEST [-1e20,1e20] v")).Single();
        // Assert
        tiny.GetValue(0).ShouldBe(7.0710678e-21m);
        near.GetValue(0).ShouldBe(1.13137084989848e-14m);
        wide.GetValue(0).ShouldBe(4e28m);
        wide.GetValue(1).ShouldBe(2e14m);
        root.GetValue(0).ShouldBeOfType<decimal>().ShouldBe(1.4142135623730951e20m, 100000m);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates: SELECT source and RETURNING share one retained statement budget")]
    public async Task Execute_AggregateSourceReturningBudget_ShouldRollbackCombinedRetentionAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("combined-budget", new() { StatementMemoryBudget = 1000 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.CreateCollectionAsync("sink", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{})", cancellationToken: CancellationToken.None);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session,
            "INSERT INTO sink (KEY k, VALUE v) SELECT 'new' AS k, {x:ARRAY_AGG(v)} AS v FROM items d UNNEST [1,2] v RETURNING *"));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        (await RowsAsync(session, "SELECT COUNT(*) FROM sink")).Single().GetValue(0).ShouldBe(0L);
    }
}
