using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphScalarRowAllocationTests
{
    private readonly ITestOutputHelper _output;
    public GraphScalarRowAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Scalar rows: both session kinds retain the tuple allocation and bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encode_ScalarResult_ShouldAllocateOnlyPayload(bool negotiated)
    {
        // Arrange
        await using var result = new GraphQueryResult([new DatabaseColumn { Name = "x", Ordinal = 0, Type = DatabaseType.Null }], [new object?[] { 42L }]);
        DatabaseRow? row = null;
        await foreach (var current in result.GetRowsAsync(CancellationToken.None)) { row = current; }
        row.ShouldNotBeNull();
        using var encoder = new GraphResultRowEncoder(new DatabaseKeyWriter(32), negotiated ? new[] { false } : null);
        // Act: tiered compilation can settle after several complete measurement batches.
        long previous = -1, allocated = 0;
        int bytes = 0, batches = 0;
        do
        {
            previous = allocated;
            long before = GC.GetAllocatedBytesForCurrentThread();
            bytes = 0;
            for (int i = 0; i < 10_000; i++) { bytes += encoder.Encode(row).Length; }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            batches++;
        } while ((batches < 2 || allocated != previous) && batches < 100);
        // Assert
        allocated.ShouldBe(previous, "two successive measurement batches must reach steady state");
        bytes.ShouldBe(90_000);
        allocated.ShouldBe(400_000);
        encoder.Encode(row).ShouldBe(DatabaseValueCodec.EncodeComponent(42L));
        _output.WriteLine($"{(negotiated ? "Negotiated" : "Legacy")}: {allocated / 10_000} B/row, {bytes / 10_000} wire bytes/row");
    }
}
