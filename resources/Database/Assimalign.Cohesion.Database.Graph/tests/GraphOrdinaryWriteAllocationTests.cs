using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    [Theory(DisplayName = "Cohesion Test [Graph] - typed label writes retain one-decode allocations and 128 B headroom (#1806)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_TypedWrites_ShouldGuardDeclarationDecode(bool injected)
    {
        // Arrange / Act
        var minima = await MeasureAsync(injected ? 0 : -1, injected ? 129 : 1024, typed: true);

        // Assert
        _output.WriteLine($"typed writes bytes/statement: CREATE={minima[0]}; SET={minima[1]}; DELETE={minima[2]}");
        if (injected) { Should.Throw<ShouldAssertException>(() => CheckTypedCeilings(minima)); }
        else { CheckTypedCeilings(minima); }
    }

    private static void CheckTypedCeilings(long[] minima)
    {
        minima[0].ShouldBeLessThanOrEqualTo(40_072 + 128);
        minima[1].ShouldBeLessThanOrEqualTo(67_896 + 128);
        minima[2].ShouldBeLessThanOrEqualTo(50_032 + 128);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - ordinary CREATE SET DELETE retain catalog v3 allocation minima (#1735, #1806)")]
    public async Task Execute_OrdinaryWrites_ShouldRetainBaseAllocations()
    {
        // Arrange / Act: a fresh process warms its own engine/session and measures on one thread.
        // Running only a warm-up on the runner's flow did not consistently remove the 4096 B
        // standalone/suite offset. Fixed catalog identities also keep the posting layout reproducible.
        // Do not treat the old offset as allocation headroom or attribute it to workers.
        var minima = await MeasureAsync(-1);

        // Assert: catalog v3 fresh-process minima plus exactly 128 B of headroom.
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
        // Native ARM64 and --arch x64 agree; each fixture takes the minimum over five
        // warmed 100-statement rounds. Keep exactly 128 B above those baselines.
        minima[0].ShouldBeLessThanOrEqualTo(38_728 + 128);
        minima[1].ShouldBeLessThanOrEqualTo(66_568 + 128);
        minima[2].ShouldBeLessThanOrEqualTo(50_032 + 128);
    }

    private static async Task<long[]> MeasureAsync(int injectedKind, int injectedBytes = 1024, bool typed = false)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "GraphRecoveryFixture", "Assimalign.Cohesion.Database.Graph.RecoveryFixture.dll");
        // PATH can select arm64 even when --arch x64 runs an x64 testhost.
        string host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(typed ? "typed-allocations" : "allocations");
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
        (await output).ShouldContain("architecture=" + RuntimeInformation.ProcessArchitecture, Case.Sensitive);
        string line = (await output).Split('\n').Single(value => value.StartsWith("allocations=", StringComparison.Ordinal));
        return line["allocations=".Length..].Trim().Split(',').Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();
    }
#endif
}
