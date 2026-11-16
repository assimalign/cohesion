using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[Collection(DatabaseProvisioningHealthCollection.Name)]
public sealed class DatabaseHealthAllocationTests
{
    private readonly ITestOutputHelper _output;
    public DatabaseHealthAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health with no failed-open records retains the base allocation pin (#1790)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_WithNoFailedOpens_ShouldAllocateBaseBytes(bool components)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.CreateBuilder("allocation").Build();
        await engine.CreateDatabaseAsync("stored", CancellationToken.None);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();
        for (int index = 0; index < 1000; index++) { Invoke(); }

        // Act
        long allocated = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++) { Invoke(); }
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert: the identical harness is measured against f9cd7efa3 in both configurations.
        _output.WriteLine($"Health bytes per 100 calls: {allocated}");
#if DEBUG
        allocated.ShouldBe(components ? 1469600 : 656800);
#else
        allocated.ShouldBe(components ? 1440000 : 638400);
#endif

        void Invoke()
        {
            if (components) { _ = application.Context.GetHealthAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            else { _ = application.Context.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        }
    }
}
