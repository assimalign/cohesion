#if !DEBUG
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentWindowFreeAllocationTests
{
    private readonly ITestOutputHelper _output;
    public DocumentWindowFreeAllocationTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Documents] - Window-free JOIN planning keeps baseline allocation bytes (#1805)")]
    public async Task Plan_WindowFreeJoin_ShouldKeepExactAllocationBytesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-allocation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("orders", CancellationToken.None);
        await session.CreateCollectionAsync("customers", CancellationToken.None);
        var statement = DocumentQueryRequest.FromOql("SELECT o.id FROM orders o JOIN customers c ON META(c).id = o.cid WHERE o.total > 100").Statement.OqlExpression;
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
        _output.WriteLine($"JOIN bytes={bytes}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        bytes.ShouldBe(11824L);
    }
    [Fact(DisplayName = "Cohesion Test [Database] - Window-free NestedChildren iterator retains base instance fields (#1805)")]
    public void Inspect_SharedIterator_ShouldKeepBaseFieldCount()
    {
        // Arrange
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.Documents.dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        int matches = 0;
        // Act / Assert
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (!metadata.GetString(type.Name).StartsWith("<NestedChildren>", StringComparison.Ordinal)) { continue; }
            _output.WriteLine($"NestedChildren fields={type.GetFields().Count}");
            type.GetFields().Count.ShouldBe(21);
            matches++;
        }
        matches.ShouldBe(1);
    }
}
#endif
