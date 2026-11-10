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
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Execute_TypedWrites_ShouldGuardDeclarationDecode(int injectedKind)
    {
        // Arrange / Act
        var minima = await MeasureAsync(injectedKind, 129, typed: true);

        // Assert
        _output.WriteLine($"typed writes bytes/statement: CREATE={minima[0]}; SET={minima[1]}; DELETE={minima[2]}");
        if (injectedKind >= 0) { Should.Throw<ShouldAssertException>(() => CheckTypedCeilings(minima)); }
        else { CheckTypedCeilings(minima); }
    }

    private static void CheckTypedCeilings(long[] minima)
    {
        minima[0].ShouldBeLessThanOrEqualTo(39_896 + 128);
        minima[1].ShouldBeLessThanOrEqualTo(67_272 + 128);
        minima[2].ShouldBeLessThanOrEqualTo(49_568 + 128);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - ordinary CREATE SET DELETE retain strict-codec allocation minima (#1735, #1806)")]
    public async Task Execute_OrdinaryWrites_ShouldRetainBaseAllocations()
    {
        // Arrange / Act: a fresh process warms its own engine/session and measures on one thread.
        // Running only a warm-up on the runner's flow did not consistently remove the 4096 B
        // standalone/suite offset. Fixed catalog identities also keep the posting layout reproducible.
        // Do not treat the old offset as allocation headroom or attribute it to workers.
        var minima = await MeasureAsync(-1);

        // Assert: shared strict-codec fresh-process minima plus exactly 128 B of headroom.
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
        minima[0].ShouldBeLessThanOrEqualTo(38_552 + 128);
        minima[1].ShouldBeLessThanOrEqualTo(65_944 + 128);
        minima[2].ShouldBeLessThanOrEqualTo(49_568 + 128);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Exact-index write allocations retain measured minima (#1644)")]
    [InlineData(-1)] [InlineData(1)] [InlineData(2)]
    public async Task Execute_IndexedWrites_ShouldGuardUpdateAndDeleteAsync(int injectedKind)
    {
        // Arrange / Act
        var minima = await MeasureAsync(injectedKind, 129, indexed: true);
        // Assert: fresh-process minima plus 128 B; injected 129-byte payloads must trip it.
        _output.WriteLine($"indexed writes bytes/statement: CREATE={minima[0]}; SET={minima[1]}; DELETE={minima[2]}");
        if (injectedKind >= 0) { Should.Throw<ShouldAssertException>(() => CheckIndexedCeilings(minima)); }
        else { CheckIndexedCeilings(minima); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Unrelated text indexes preserve measured exact-index allocation minima (#1808)")]
    [InlineData(-1)] [InlineData(1)] [InlineData(2)]
    public async Task Execute_Fix4TextIndexedWrites_ShouldKeepAllocations(int injectedKind)
    {
        // Arrange / Act.
        var minima = await MeasureAsync(injectedKind, 129, indexed: true, textIndexed: true);
        // Assert: the enum checks lowered DELETE's measured floor by 248 B from
        // ffa322e6e. Keep only 128 B headroom, with the same injected controls.
        _output.WriteLine($"text-indexed writes bytes/statement: CREATE={minima[0]}; SET={minima[1]}; DELETE={minima[2]}");
        if (injectedKind >= 0) { Should.Throw<ShouldAssertException>(() => CheckTextIndexedCeilings(minima)); }
        else { CheckTextIndexedCeilings(minima); }
    }

    private static void CheckTextIndexedCeilings(long[] minima)
    {
        minima[0].ShouldBeLessThanOrEqualTo(43_784 + 128);
        minima[1].ShouldBeLessThanOrEqualTo(73_320 + 128);
        minima[2].ShouldBeLessThanOrEqualTo(54_136 + 128);
    }

    private static void CheckIndexedCeilings(long[] minima)
    {
        minima[0].ShouldBeLessThanOrEqualTo(42_256 + 128);
        minima[1].ShouldBeLessThanOrEqualTo(71_760 + 128);
        minima[2].ShouldBeLessThanOrEqualTo(53_264 + 128);
    }

    private static async Task<long[]> MeasureAsync(int injectedKind, int injectedBytes = 1024, bool typed = false, bool indexed = false, bool textIndexed = false)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "GraphRecoveryFixture", "Assimalign.Cohesion.Database.Graph.RecoveryFixture.dll");
        // PATH can select arm64 even when --arch x64 runs an x64 testhost.
        string host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // Pin optimized JIT code in this measurement child: background tiering changed CREATE
        // by 144 B depending on whether a control allocation was injected. The measured ceilings
        // preserve 128 B headroom and injected 129 B/1 KiB controls; ordinary execution keeps its defaults.
        start.Environment["DOTNET_TieredCompilation"] = "0";
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(textIndexed ? "text-indexed-allocations" : indexed ? "indexed-allocations" : typed ? "typed-allocations" : "allocations");
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
