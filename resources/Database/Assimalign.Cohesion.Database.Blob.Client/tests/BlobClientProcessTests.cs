using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>Real-process proof that both ends of the wire stream below object size.</summary>
public sealed class BlobClientProcessTests
{
    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Wire streaming: 256 MiB round-trips with a shared 64 MiB heap")]
    public async Task UploadDownload_ObjectFourTimesLargerThanAvailableHeap_ShouldRoundTripOverWire()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-blob-wire-process", Guid.NewGuid().ToString("N"));
        string fixture = Path.Combine(AppContext.BaseDirectory, "BlobClientStreamingFixture", "Assimalign.Cohesion.Database.Blob.Client.StreamingFixture.dll");
        File.Exists(fixture).ShouldBeTrue($"Fixture was not copied to test output: {fixture}");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(root);
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x4000000";
        start.Environment["DOTNET_GCServer"] = "0";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start wire streaming fixture.");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                process.ExitCode.ShouldBe(0, $"Fixture output:\n{stdout}\n{stderr}");
                string[] fields = stdout.Trim().Split('|');
                fields.Length.ShouldBe(5, stdout);
                fields[0].ShouldBe("WIRE_ROUNDTRIP_OK");
                long length = long.Parse(fields[1], CultureInfo.InvariantCulture);
                long available = long.Parse(fields[2], CultureInfo.InvariantCulture);
                length.ShouldBe(256L * 1024 * 1024 + 123);
                available.ShouldBeGreaterThan(0);
                available.ShouldBeLessThanOrEqualTo(64L * 1024 * 1024);
                length.ShouldBeGreaterThan(4 * available);
                fields[3].Length.ShouldBe(64);
                int.Parse(fields[4], CultureInfo.InvariantCulture).ShouldBeLessThanOrEqualTo(BlobProtocol.MaxChunkLength);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
