using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentStatementMemoryTests
{
    private readonly ITestOutputHelper _output;

    public DocumentStatementMemoryTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Documents] - ARRAY_AGG: a genuinely oversized result still refuses")]
    public async Task Execute_OversizedArrayResult_ShouldRefuseAsync()
    {
        // Arrange: the array alone needs over 1.2 MB of UTF-8, before ownership metadata.
        await using var engine = DocumentDatabaseEngine.Create("oversized-array", new() { StatementMemoryBudget = 1024 * 1024 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{})", cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["v"] = new string('x', 400_000) };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "SELECT ARRAY_AGG(@v) FROM items UNNEST [1,2,3] x", parameters, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        failure.Diagnostics[0].Start.ShouldBe(7);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Statement budget: source and group reservations release on every executor exit")]
    [InlineData("success")]
    [InlineData("source")]
    [InlineData("group")]
    [InlineData("cancel")]
    public async Task Execute_StagingLifetime_ShouldReleaseOnEveryExitAsync(string scenario)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var builder = DocumentDatabaseEngine.CreateBuilder("staging-lifetimes");
        builder.Options.StatementMemoryBudget = 8192;
        int calls = 0;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("cancel_group", value =>
        { calls++; cancellation.Cancel(); return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string padding = scenario == "source" ? new string('x', 8192) : "x";
        await collection.PutAsync(session, "a", System.Text.Encoding.UTF8.GetBytes("{\"g\":1,\"padding\":\"" + padding + "\"}"), cancellationToken: CancellationToken.None);
        string query = scenario switch
        {
            "group" => "SELECT COUNT(*) FROM items GROUP BY @g",
            "cancel" => "SELECT ARRAY_AGG(cancel_group(g)) FROM items GROUP BY g",
            _ => "SELECT COUNT(*) FROM items GROUP BY g",
        };
        var request = DocumentQueryRequest.FromOql(query);
        var parameters = new Dictionary<string, object?> { ["g"] = new string('p', 8192) };
        DocumentAggregateMemory? observed = null;

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            observed = operation.GetStatementMemory(cancellation.Token);
            if (scenario == "success")
            {
                await using var result = (await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, cancellation.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                int rows = 0;
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetInt64(0).ShouldBe(1L); rows++; }
                rows.ShouldBe(1);
                observed.Bytes.ShouldBe(320L); // Only the returned scalar row survives staging.
            }
            else
            {
                if (scenario == "cancel")
                {
                    await Should.ThrowAsync<OperationCanceledException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, cancellation.Token));
                    calls.ShouldBe(1);
                }
                else
                {
                    (await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, cancellation.Token)))
                        .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
                }
                observed.Bytes.ShouldBe(0L);
            }
            return true;
        }, CancellationToken.None);
        observed.ShouldNotBeNull().Bytes.ShouldBe(0L); // Operation completion also releases result charges.
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - ARRAY_AGG: HAVING and sort scratch stay within one MiB")]
    public async Task Execute_ArrayAggregateScratch_ShouldReleaseBetweenGroupsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("array-scratch", new() { StatementMemoryBudget = 1024 * 1024 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int index = 0; index < 40; index++)
        {
            string key = index.ToString(CultureInfo.InvariantCulture);
            await collection.PutAsync(session, new(key), System.Text.Encoding.UTF8.GetBytes("{\"g\":" + key + "}"), cancellationToken: CancellationToken.None);
        }
        var parameters = new Dictionary<string, object?> { ["v"] = new string('x', 2000) };

        // Act
        var request = DocumentQueryRequest.FromOql("SELECT ARRAY_AGG(@v) FROM items GROUP BY g HAVING ARRAY_LENGTH(ARRAY_AGG(@v)) > 0 ORDER BY ARRAY_AGG(@v)");
        long peak = 0, final = 0;
        long peakLive = 0, resultLive = 0;
        await using var result = (await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            long baseline = 0;
            memory.RetentionObserved = () => peakLive = Math.Max(peakLive, GC.GetTotalMemory(true) - baseline);
            baseline = GC.GetTotalMemory(true);
            var result = await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None);
            memory.RetentionObserved = null;
            peak = memory.PeakBytes;
            final = memory.Bytes;
            resultLive = GC.GetTotalMemory(true) - baseline;
            return result;
        }, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.GetValue(0).ShouldBeOfType<System.Text.Json.JsonElement>()[0].GetString().ShouldBe(parameters["v"]);
            rows++;
        }
        rows.ShouldBe(40);
        _output.WriteLine($"ARRAY_AGG: peak charge={peak}; retained output charge={final}; measured live peak={peakLive}; live result={resultLive}. Review: charge=1962240; live peak=221944; live result=92928.");
        peak.ShouldBeLessThanOrEqualTo(2L * peakLive);
        final.ShouldBe(98_240L); // Forty independently owned arrays and row storage; no HAVING/order input lists.
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Statement budget: padded source and 5000 groups refuse before publication")]
    public async Task Execute_PaddedGroups_ShouldRefuseAtStatementBudgetAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("group-budget", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None))
        {
            for (int index = 0; index < 5000; index++)
            {
                string key = index.ToString("D5", CultureInfo.InvariantCulture);
                await collection.PutAsync(session, new(key), System.Text.Encoding.UTF8.GetBytes("{\"g\":\"" + key + new string('p', 2048) + "\"}"), cancellationToken: CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT COUNT(*) FROM items GROUP BY g", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        (await collection.GetAsync(session, "00000", CancellationToken.None)).ShouldNotBeNull();
    }
}
