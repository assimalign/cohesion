using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphOrdinaryWriteAllocationTests
{
    private readonly ITestOutputHelper _output;

    public GraphOrdinaryWriteAllocationTests(ITestOutputHelper output) { _output = output; }

#if !DEBUG
    [Fact(DisplayName = "Cohesion Test [Graph] - ordinary CREATE SET DELETE retain pre-MERGE-fix allocation minima (#1735)")]
    public async Task Execute_OrdinaryWrites_ShouldRetainBaseAllocations()
    {
        // Arrange / Act: a fresh process warms its own engine/session and measures on one thread.
        // Running only a warm-up on the runner's flow did not consistently remove the 4096 B
        // standalone/suite offset. Fixed catalog identities also keep the posting layout reproducible.
        // Do not treat the old offset as allocation headroom or attribute it to workers.
        var minima = await MeasureAsync(-1);

        // Assert: isolated third-pass minima plus exactly 128 B of headroom.
        _output.WriteLine($"ordinary writes bytes/statement: CREATE={minima[0]}; SET={minima[1]}; DELETE={minima[2]}");
        CheckCeilings(minima);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - ordinary write allocation guards reject a 1 KiB regression in isolation (#1735)")]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Execute_InjectedAllocation_ShouldTripGuard(int kind)
    {
        // Arrange / Act: inject one allocation inside the measured statement in the same fixture.
        var minima = await MeasureAsync(kind);

        // Assert: the former standalone CREATE and DELETE guards accepted this extra allocation.
        _output.WriteLine($"injected 1024 B payload, kind={kind}: CREATE={minima[0]}; SET={minima[1]}; DELETE={minima[2]}");
        Should.Throw<ShouldAssertException>(() => CheckCeilings(minima));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - SET and DELETE allocation guards retain only 128 bytes of headroom (#1735)")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Execute_JustOverHeadroom_ShouldTripGuard(int kind)
    {
        // Arrange / Act: a 129-byte payload allocates 160 bytes including the array header.
        var minima = await MeasureAsync(kind, 129);

        // Assert: stale SET and DELETE ceilings allowed this allocation.
        _output.WriteLine($"injected 129 B payload, kind={kind}: CREATE={minima[0]}; SET={minima[1]}; DELETE={minima[2]}");
        Should.Throw<ShouldAssertException>(() => CheckCeilings(minima));
    }

    private static void CheckCeilings(long[] minima)
    {
        minima[0].ShouldBeLessThanOrEqualTo(38_952 + 128);
        minima[1].ShouldBeLessThanOrEqualTo(66_888 + 128);
        minima[2].ShouldBeLessThanOrEqualTo(50_088 + 128);
    }

    private static async Task<long[]> MeasureAsync(int injectedKind, int injectedBytes = 1024)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "GraphRecoveryFixture", "Assimalign.Cohesion.Database.Graph.RecoveryFixture.dll");
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add("allocations");
        start.ArgumentList.Add(injectedKind.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(injectedBytes.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start graph allocation fixture.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cleanup.Token);
            throw;
        }
        process.ExitCode.ShouldBe(0, await output + await error);
        string line = (await output).Split('\n').Single(value => value.StartsWith("allocations=", StringComparison.Ordinal));
        return line["allocations=".Length..].Trim().Split(',').Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();
    }
#endif
}
