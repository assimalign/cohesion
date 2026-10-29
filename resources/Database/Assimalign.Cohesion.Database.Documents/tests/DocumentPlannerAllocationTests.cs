#if !DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentPlannerAllocationTests
{
    private readonly ITestOutputHelper _output;

    public DocumentPlannerAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - ANY and UNNEST plans without subqueries keep their original allocation bytes")]
    [InlineData("SELECT RAW 1 FROM items d WHERE ANY v IN d.tags SATISFIES v.n = 1 END", 8680L)]
    [InlineData("SELECT RAW 1 FROM items d UNNEST d.tags AS v WHERE v.n = 1", 9880L)]
    public async Task Plan_ArrayScopeWithoutSubqueries_ShouldKeepExactAllocationBytesAsync(string query, long expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("array-planning-allocations", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON items (DISTINCT ARRAY v.n FOR v IN tags END)", cancellationToken: CancellationToken.None);
        var statement = DocumentQueryRequest.FromOql(query).Statement.OqlExpression;
        // Act
        long bytes = await database.RunAsync(session, operation =>
        {
            DocumentStatementPlan Plan() => new DocumentPlanner(database.Catalog, operation.Context.Snapshot, null).Plan(statement);
            for (int i = 0; i < 100; i++) { GC.KeepAlive(Plan()); }
            long minimum = long.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++) { GC.KeepAlive(Plan()); }
                minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return ValueTask.FromResult(minimum / 100);
        }, CancellationToken.None);
        // Assert
        _output.WriteLine($"{query}: {bytes} bytes/plan");
        bytes.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Scalar planning retains its base allocation budget (#1530)")]
    public async Task Plan_ScalarPredicateWithThreeIndexes_ShouldKeepBaseAllocationBudgetAsync()
    {
        // Arrange: parse and acquire the snapshot outside the synchronous measured loop.
        await using var engine = DocumentDatabaseEngine.Create("planning-allocations", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX c ON items (category)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX a ON items (amount)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX n ON items (name)", cancellationToken: CancellationToken.None);
        var statement = DocumentQueryRequest.FromOql("SELECT META().id FROM items WHERE category = 'a' AND amount > 1").Statement.OqlExpression;
        long bytes = await database.RunAsync(session, operation =>
        {
            DocumentStatementPlan Plan() => new DocumentPlanner(database.Catalog, operation.Context.Snapshot, null).Plan(statement);
            for (int i = 0; i < 100; i++) { GC.KeepAlive(Plan()); }
            long minimum = long.MaxValue;

            // Act: five rounds of 100 plans without an await moving the allocation counter.
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++) { GC.KeepAlive(Plan()); }
                minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return ValueTask.FromResult(minimum / 100);
        }, CancellationToken.None);

        // Assert: review measured e0e776aac at 15,832 bytes/plan. With these short index
        // names the same base ChooseAccess measured 15,656; afb137f8f measured 15,848.
        _output.WriteLine($"Scalar planning: {bytes:N0} bytes/plan; review base=15,832; isolated base=15,656.");
        bytes.ShouldBeLessThanOrEqualTo(15_832L);
        bytes.ShouldBeLessThanOrEqualTo(15_656L);
    }
}
#endif
