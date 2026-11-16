#if !DEBUG
using System;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlAnalysisAllocationTests
{
    private readonly ITestOutputHelper _output;
    public SqlAnalysisAllocationTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join-free analysis scopes avoid an eager policy dictionary (#1789)")]
    public void BeginAnalysis_JoinFreeScope_ShouldAvoidPolicyDictionary()
    {
        // Arrange: isolate scope construction from parsing, planning and storage.
        for (int i = 0; i < 1000; i++) { Begin(); }
        long minimum = long.MaxValue;
        // Act
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { Begin(); }
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        // Assert: batch 4 uses 392 B per scope on both 64-bit architectures. Only
        // the policy field's pointer remains; its dictionary waits for a hash step.
        _output.WriteLine($"ANALYSIS_SCOPE_ALLOC|bytes={minimum / 1000}|architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        (minimum / 1000).ShouldBeLessThanOrEqualTo(392 + IntPtr.Size);

        static void Begin()
        {
            var metrics = new SqlStatementMetrics(); metrics.BeginAnalysis(); GC.KeepAlive(metrics);
        }
    }
}
#endif
