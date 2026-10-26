using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Blob.Tests.TestObjects;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Blob.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>Real-process proof that both ends of the wire stream below object size.</summary>
public sealed class BlobClientProcessTests
{
    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Crash: reopened content is committed-only over InMemory and TCP (#1713)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crash_UnfinishedUpload_ShouldRecoverCommittedContentOverWire(bool tcp)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-blob-wire-crash", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "BlobClientStreamingFixture", "Assimalign.Cohesion.Database.Blob.Client.StreamingFixture.dll"));
        start.ArgumentList.Add("crash-writer");
        start.ArgumentList.Add(root);
        try
        {
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start wire crash fixture."))
            {
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    string? ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    if (ready is null)
                    {
                        await process.WaitForExitAsync(timeout.Token);
                        throw new InvalidOperationException($"Wire crash fixture exited {process.ExitCode} before readiness: {await error}");
                    }
                    ready.ShouldBe("CRASH_READY");
                    process.HasExited.ShouldBeFalse();
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(timeout.Token);
                    process.ExitCode.ShouldNotBe(0);
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await process.WaitForExitAsync(cleanup.Token);
                }
            }

            // Act
            await using var engine = BlobDatabaseEngine.Create("blob-wire-reopen", new() { RootPath = root });
            await CrashReopen.OpenAsync(Path.Combine(root, "crash", "blob.dat"),
                token => engine.OpenDatabaseAsync("crash", token), timeout.Token);
            await using IDuplexConnectionListener listener = tcp
                ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
                : new InMemoryConnectionListener();
            await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
            await server.StartAsync(timeout.Token);
            await using var client = BlobClient.Create(new()
            {
                Settings = new DatabaseConnectionSettings { Database = "crash", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
                ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory()
            });
            await using var connection = await client.ConnectAsync(timeout.Token);

            // Assert
            (await connection.GetPropertiesAsync("objects", "incomplete", timeout.Token)).ShouldBeNull();
            (await connection.GetPropertiesAsync("objects", "committed", timeout.Token))!.Value.Length.ShouldBe(17);
            await using var download = await connection.DownloadAsync("objects", "committed", timeout.Token);
            using var content = new MemoryStream();
            await download.CopyToAsync(content, timeout.Token);
            Encoding.UTF8.GetString(content.ToArray()).ShouldBe("committed payload");
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

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
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
