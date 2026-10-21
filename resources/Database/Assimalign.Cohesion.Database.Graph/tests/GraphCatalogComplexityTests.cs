using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCatalogComplexityTests
{
    private readonly ITestOutputHelper _output;

    public GraphCatalogComplexityTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Catalog: definition discovery visits a bounded number of records per entity")]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(2000)]
    public async Task Execute_NewDefinitions_ShouldUseLinearCatalogWork(int count)
    {
        // Arrange: spread labels across nodes, keeping each physical record within its size limit.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("catalog-work", new());
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        string statement = "INSERT " + string.Join(",", Enumerable.Range(0, count)
            .Select(index => $"(:L{index} {{p: 1}})-[:T{index} {{p: 1}}]->()"));
        long reads = 0;
        long keys = 0;
        database.Catalog.BeforeRecordRead = () => reads++;
        database.Catalog.BeforeDirectoryKeyVisit = () => keys++;

        // Act
        var timer = Stopwatch.StartNew();
        await session.ExecuteAsync(statement, cancellationToken: timeout.Token);
        timer.Stop();
        database.Catalog.BeforeRecordRead = null;
        database.Catalog.BeforeDirectoryKeyVisit = null;

        // Assert: deterministic work bounds, independent of machine speed and timer resolution.
        _output.WriteLine($"Definitions={count * 2}; CatalogReads={reads}; DirectoryKeyVisits={keys}; ElapsedMs={timer.Elapsed.TotalMilliseconds:F2}");
        reads.ShouldBeLessThanOrEqualTo(40L * count);
        keys.ShouldBeLessThanOrEqualTo(8L * count);
        (await GraphSchema.Open(database, session).GetLabelsAsync(timeout.Token)).Count.ShouldBe(count);
        (await GraphSchema.Open(database, session).GetRelationshipTypesAsync(timeout.Token)).Count.ShouldBe(count);
    }
}
