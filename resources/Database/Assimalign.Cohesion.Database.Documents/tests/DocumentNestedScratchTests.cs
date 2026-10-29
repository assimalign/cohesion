using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentNestedScratchTests
{
    private readonly ITestOutputHelper _output;

    public DocumentNestedScratchTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Documents] - Correlation: object maps retain live member results until serialization")]
    public async Task ExecuteAsync_RetainedObjectMembers_ShouldEnforceBudgetAndReleaseAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("retained-members");
        builder.Options.StatementMemoryBudget = 256 * 1024;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("payload", value => { calls++; return new string('x', 8192); }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string members = string.Join(",", Enumerable.Range(0, 100).Select(n => "a" + n + ": (SELECT RAW payload(d) FROM [1] AS s)"));
        var request = DocumentQueryRequest.FromOql("SELECT RAW {" + members + "} FROM [1] AS d");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            calls.ShouldBeGreaterThan(1);
            calls.ShouldBeLessThan(100);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Correlation: element scratch preserves cached collection and nested cache reservations")]
    public async Task ExecuteAsync_CachedCollection_ShouldPreserveReservationsAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("retained-collection");
        builder.Options.StatementMemoryBudget = 256 * 1024;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("payload", value => { calls++; return new string('x', 8192); }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var numbers = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 100)) + "]");
        var parameters = new Dictionary<string, object?> { ["numbers"] = numbers.RootElement };
        var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM [1] AS d WHERE EVERY v IN (SELECT RAW v FROM @numbers AS v) SATISFIES EXISTS (SELECT RAW payload(v) FROM [1] AS s) AND EXISTS (SELECT RAW 1 FROM [1] AS c) END");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None))
            {
                int count = 0;
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
                count.ShouldBe(1);
            }
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
        calls.ShouldBe(100);
        session.LastQuerySubqueriesExecuted.ShouldBe(102);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: discarded per-element results release scratch")]
    [InlineData("EVERY v IN @numbers SATISFIES EXISTS {v} END", 1000, 1)]
    [InlineData("ANY v IN @numbers SATISFIES NOT EXISTS {v} END", 1000, 0)]
    [InlineData("EXISTS ARRAY 1 FOR v IN @numbers WHEN EXISTS {v} END", 1000, 1)]
    [InlineData("EXISTS FIRST 1 FOR v IN @numbers WHEN NOT EXISTS {v} END", 1000, 0)]
    [InlineData("EVERY v IN @numbers SATISFIES 1 IN (ARRAY_LENGTH({v}), 1) END", 1000, 1)]
    [InlineData("EVERY v IN @numbers SATISFIES CASE WHEN EXISTS {v} THEN TRUE END END", 1000, 1)]
    public async Task ExecuteAsync_ElementScratch_ShouldRemainWithinBudgetAsync(string form, int expectedCalls, int expectedRows)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("element-scratch");
        builder.Options.StatementMemoryBudget = 256 * 1024;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("payload", value => { calls++; return new string('x', 8192); }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var numbers = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 1000)) + "]");
        var parameters = new Dictionary<string, object?> { ["numbers"] = numbers.RootElement };
        // Act / Assert: both forms retain the same input collection, callbacks and truth values.
        foreach (string operand in new[] { "[payload(v)]", "(SELECT RAW payload(v) FROM [1] AS s)" })
        {
            calls = 0;
            var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM [1] AS d WHERE " + form.Replace("{v}", operand, StringComparison.Ordinal));
            try { await database.RunAsync(session, async operation =>
            {
                var memory = operation.GetStatementMemory(CancellationToken.None);
                await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None))
                {
                    int count = 0;
                    await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
                    count.ShouldBe(expectedRows);
                }
                memory.Bytes.ShouldBe(0);
                return true;
            }, CancellationToken.None); }
            finally { _output.WriteLine($"Operand={operand}; callbacks={calls}"); }
            calls.ShouldBe(expectedCalls);
        }
    }

    public static IEnumerable<object[]> DiscardingLoops()
    {
        const string nested = "(SELECT RAW payload(d) FROM [1] AS s)";
        const int count = 100;
        yield return ["1 IN (" + string.Join(",", Enumerable.Repeat("ARRAY_LENGTH(" + nested + ") + 1", count)) + ")", count, false];
        yield return ["CASE " + string.Concat(Enumerable.Repeat("WHEN NOT EXISTS " + nested + " THEN FALSE ", count)) + "ELSE TRUE END", count, true];
        yield return ["IFNULL(" + string.Join(",", Enumerable.Repeat("CASE WHEN EXISTS " + nested + " THEN NULL END", count)) + ", TRUE)", count, true];
        yield return ["{" + string.Join(",", Enumerable.Range(0, count).Select(n => "a" + n + ": EXISTS " + nested)) + "} IS NOT NULL", count, true];
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: IN CASE conditional calls and object members release discarded operands")]
    [MemberData(nameof(DiscardingLoops))]
    public async Task ExecuteAsync_DiscardingLoop_ShouldReleaseEachOperandAsync(string predicate, int expectedCalls, bool expected)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("discarded-operands");
        builder.Options.StatementMemoryBudget = 256 * 1024;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("payload", value => { calls++; return new string('x', 8192); }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW 1 FROM [1] AS d WHERE " + predicate);
        // Assert
        rows.Count.ShouldBe(expected ? 1 : 0);
        calls.ShouldBe(expectedCalls);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Construction: returned values stay charged while a binary consumer holds them")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("first")]
    public async Task ExecuteAsync_ConstructedOperands_ShouldRefuseCombinedLiveValuesAsync(string kind)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("constructed-charge");
        builder.Options.StatementMemoryBudget = 256 * 1024;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("payload", value => { calls++; return new string('x', 8192); }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string members = string.Join(",", Enumerable.Range(0, 20).Select(n => "a" + n + ":(SELECT RAW payload(d) FROM [1] AS s)"));
        string operand = kind switch
        {
            "object" => "{" + members + "}",
            "array" => "ARRAY (SELECT RAW payload(v) FROM [1] AS s) FOR v IN @numbers END",
            _ => "FIRST {" + members + "} FOR v IN [d] END",
        };
        using var numbers = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 20)) + "]");
        var parameters = new Dictionary<string, object?> { ["numbers"] = numbers.RootElement };
        var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM [1] AS d WHERE " + operand + " = " + operand);
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            calls.ShouldBeGreaterThan(20);
            calls.ShouldBeLessThanOrEqualTo(40);
            memory.Bytes.ShouldBe(0);
            _output.WriteLine($"{kind}: callbacks={calls}; peak={memory.PeakBytes}; final={memory.Bytes}");
            return true;
        }, CancellationToken.None);
    }
}
