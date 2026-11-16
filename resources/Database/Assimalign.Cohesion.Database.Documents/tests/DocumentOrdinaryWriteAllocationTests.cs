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

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentOrdinaryWriteAllocationTests
{
    private readonly ITestOutputHelper _output;
    public DocumentOrdinaryWriteAllocationTests(ITestOutputHelper output) { _output = output; }

#if !DEBUG
    [Theory(DisplayName = "Cohesion Test [Documents] - Ordinary PUT and DELETE retain base allocations (#1808)")]
    [InlineData(false, 0)] [InlineData(false, 129)]
    [InlineData(true, 0)] [InlineData(true, 129)]
    public async Task PutAsync_OrdinaryWrite_ShouldRetainMeasuredBase(bool indexed, int injectedBytes)
    {
        // Arrange: optimized, fresh-process minima on one thread, with a positive control.
        string fixture = Path.Combine(AppContext.BaseDirectory, "DocumentRecoveryFixture", "Assimalign.Cohesion.Database.Documents.RecoveryFixture.dll");
        string host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DOTNET_TieredCompilation"] = "0";
        start.ArgumentList.Add(fixture); start.ArgumentList.Add(indexed ? "allocations-indexed" : "allocations"); start.ArgumentList.Add(injectedBytes.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act.
        try { await process.WaitForExitAsync(guard.Token); }
        catch { if (!process.HasExited) { process.Kill(true); } await process.WaitForExitAsync(CancellationToken.None); throw; }
        string text = await output;
        process.ExitCode.ShouldBe(0, text + await error);
        _output.WriteLine(text);
        text.ShouldContain("architecture=" + RuntimeInformation.ProcessArchitecture, Case.Sensitive);
        var bytes = text.Split(';')[0]["allocations=".Length..].Split(',').Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();

        // Assert: review-base minima are identical on ARM64 and x64. Keep 128 bytes
        // of runtime headroom; the injected 129-byte array must exceed that cap.
        long putCeiling = (indexed ? 35920L : 23224L) + 128;
        long deleteCeiling = (indexed ? 26736L : 14160L) + 128;
        void Check() { bytes[0].ShouldBeLessThanOrEqualTo(putCeiling); bytes[1].ShouldBeLessThanOrEqualTo(deleteCeiling); }
        if (injectedBytes == 0) { Check(); }
        else { Should.Throw<ShouldAssertException>(Check); }
    }
#endif
}
