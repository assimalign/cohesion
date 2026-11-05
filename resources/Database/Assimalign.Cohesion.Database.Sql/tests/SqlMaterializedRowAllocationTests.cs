using System;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlMaterializedRowAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlMaterializedRowAllocationTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Materialized rows allocate only their wrapper through base and typed access")]
    public void Accessors_CompatibleValues_ShouldAllocateOnlyTheRow()
    {
        // Arrange: borrow preboxed values so only wrapper and accessor costs are measured.
        object?[] values = [42, 42L, true, 42d, "text", new byte[] { 1, 2 }, null];
        const int iterations = 10_000;
        Measure(values, iterations, read: true);
        Measure(values, iterations, read: false);

        // Act: compare identical row construction with and without base-typed access.
        long construction = long.MaxValue;
        long access = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            construction = Math.Min(construction, Measure(values, iterations, read: false));
            access = Math.Min(access, Measure(values, iterations, read: true));
        }
        _output.WriteLine($"Construction: {construction / (double)iterations} B/row; with accessors: {access / (double)iterations} B/row");

        // Assert: the pre-NVI wrapper is 32 bytes on 64-bit runtimes; allow layout headroom.
        construction.ShouldBeGreaterThan(0);
        construction.ShouldBeLessThanOrEqualTo(40L * iterations);
        access.ShouldBe(construction);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(object?[] values, int iterations, bool read)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            DatabaseRow row = CreateRow(values);
            if (read)
            {
                if (row.GetInt32(0) != 42 || row.GetInt64(1) != 42 || !row.GetBoolean(2)
                    || row.GetDouble(3) != 42 || row.GetString(4) != "text"
                    || row.GetBytes(5).Length != 2 || !row.IsNull(6)
                    || !ReferenceEquals(row.GetValue(0), values[0]))
                {
                    throw new InvalidOperationException();
                }
            }
            GC.KeepAlive(row);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DatabaseRow CreateRow(object?[] values) => new SqlMaterializedRow(values);
}
