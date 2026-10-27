using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentGroupMemoryTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - GROUP AS: wrapper retention exhausts the shared grant after ordinary grouping fits")]
    public async Task Execute_GroupWrapperBudget_ShouldRefuseAndReleaseAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("wrapper-budget", new() { StatementMemoryBudget = 8192 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('p', 1200) + "\"}"), cancellationToken: CancellationToken.None);
        await using var control = (await session.ExecuteAsync("SELECT COUNT(*) FROM items UNNEST [1,2,3,4] x GROUP BY TRUE", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var request = DocumentQueryRequest.FromOql("SELECT ARRAY_LENGTH(g) FROM items UNNEST [1,2,3,4] x GROUP BY TRUE GROUP AS g");

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            failure.Diagnostics[0].Start.ShouldNotBeNull();
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - GROUP AS: reservations release on success projection failure and deterministic cancellation")]
    [InlineData("success")]
    [InlineData("projection")]
    [InlineData("cancel")]
    public async Task Execute_GroupWrapperLifetime_ShouldReleaseOnEveryExitAsync(string scenario)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = DocumentDatabaseEngine.Create("wrapper-lifetime", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('p', 1200) + "\"}"), cancellationToken: CancellationToken.None);
        long controlPeak = 0;
        var controlRequest = DocumentQueryRequest.FromOql("SELECT COUNT(*) FROM items GROUP BY TRUE");
        await database.RunAsync(session, async operation =>
        {
            await using var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, controlRequest.Statement, null, CancellationToken.None);
            controlPeak = operation.GetStatementMemory(CancellationToken.None).PeakBytes;
            return true;
        }, CancellationToken.None);
        string projection = scenario == "projection" ? "1/0" : "ARRAY_LENGTH(g)";
        var request = DocumentQueryRequest.FromOql("SELECT " + projection + " FROM items GROUP BY TRUE GROUP AS g");

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(cancellation.Token);
            if (scenario == "cancel")
            { memory.RetentionObserved = () => { if (memory.Bytes > controlPeak + 128) { cancellation.Cancel(); } }; }
            if (scenario == "success")
            {
                await using var result = (await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                memory.Bytes.ShouldBe(320L);
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(1L); }
            }
            else if (scenario == "cancel")
            {
                await Should.ThrowAsync<OperationCanceledException>(async () =>
                    await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token));
                cancellation.IsCancellationRequested.ShouldBeTrue();
            }
            else
            {
                (await Should.ThrowAsync<DatabaseException>(async () =>
                    await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token)))
                    .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
            }
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }
}
