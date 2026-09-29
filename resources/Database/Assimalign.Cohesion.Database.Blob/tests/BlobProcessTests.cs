using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// Real-process acceptance tests for bounded memory and ungraceful crash recovery.
/// </summary>
public sealed class BlobProcessTests
{
    /// <summary>
    /// A blob larger than the process's entire available GC heap round-trips and reopens.
    /// </summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Blob] - Streaming: A 128 MiB blob round-trips with a 64 MiB heap")]
    public async Task OpenStreams_BlobLargerThanAvailableMemory_ShouldRoundTrip()
    {
        string root = NewRoot();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var process = StartFixture("roundtrip", root, constrainedHeap: true);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                string stdout = await output;
                string stderr = await error;
                process.ExitCode.ShouldBe(0, $"Fixture output:\n{stdout}\n{stderr}");
                var fields = stdout.Trim().Split('|');
                fields.Length.ShouldBe(4, stdout);
                fields[0].ShouldBe("ROUNDTRIP_OK");
                long length = long.Parse(fields[1], CultureInfo.InvariantCulture);
                long available = long.Parse(fields[2], CultureInfo.InvariantCulture);
                length.ShouldBe(128L * 1024 * 1024 + 123);
                available.ShouldBeGreaterThan(0);
                available.ShouldBeLessThan(length);
                available.ShouldBeLessThanOrEqualTo(64L * 1024 * 1024);
                fields[3].Length.ShouldBe(64);
            }
            finally { await StopFixtureAsync(process); }
        }
        finally { DeleteRoot(root); }
    }

    /// <summary>
    /// Killing a process with flushed loser pages preserves the committed chain only.
    /// </summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Blob] - Crash: Committed data survives and unfinished chains stay absent")]
    public async Task Crash_WithHalfWrittenChains_ShouldRecoverOnlyCommittedBlobs()
    {
        string root = NewRoot();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            string? ready;
            using (var process = StartFixture("crash-writer", root, constrainedHeap: false))
            {
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    if (ready is null)
                    {
                        await process.WaitForExitAsync(timeout.Token);
                        throw new InvalidOperationException($"Crash fixture exited {process.ExitCode} before readiness: {await error}");
                    }
                    ready.ShouldStartWith("CRASH_READY|");
                    process.HasExited.ShouldBeFalse();
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(timeout.Token);
                    process.ExitCode.ShouldNotBe(0);
                }
                finally { await StopFixtureAsync(process); }
            }

            var fields = ready.Split('|');
            fields.Length.ShouldBe(3);
            long committedLength = long.Parse(fields[1], CultureInfo.InvariantCulture);
            await using var reopened = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions { RootPath = root });
            var database = (IBlobDatabase)await reopened.OpenDatabaseAsync("existing", timeout.Token);
            var container = await database.GetContainerAsync("objects", timeout.Token);
            (await container.GetPropertiesAsync("committed", timeout.Token))!.Value.Length.ShouldBe(committedLength);
            var (length, digest) = await ReadDigestAsync(container, "committed", timeout.Token);
            length.ShouldBe(committedLength);
            digest.ShouldBe(fields[2]);
            var names = new List<string>();
            await foreach (var blob in container.GetBlobsAsync(cancellationToken: timeout.Token))
            {
                names.Add(blob.Name);
            }
            names.ShouldBe(new[] { "committed" });

            var otherDatabase = (IBlobDatabase)await reopened.OpenDatabaseAsync("new-object", timeout.Token);
            var otherContainer = await otherDatabase.GetContainerAsync("objects", timeout.Token);
            (await otherContainer.GetPropertiesAsync("incomplete", timeout.Token)).ShouldBeNull();
            await Should.ThrowAsync<DatabaseException>(async () =>
                await otherContainer.OpenReadAsync("incomplete", timeout.Token));
            await foreach (var unexpected in otherContainer.GetBlobsAsync(cancellationToken: timeout.Token))
            {
                throw new InvalidDataException($"Uncommitted blob survived: {unexpected.Name}.");
            }
            reopened.State.ShouldBe(EngineState.Running);
        }
        finally { DeleteRoot(root); }
    }

    private static Process StartFixture(string mode, string root, bool constrainedHeap)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "BlobStreamingFixture", "Assimalign.Cohesion.Database.Blob.StreamingFixture.dll");
        File.Exists(fixture).ShouldBeTrue($"Fixture was not copied to the test output: {fixture}");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(root);
        if (constrainedHeap)
        {
            start.Environment["DOTNET_GCHeapHardLimit"] = "0x4000000";
            start.Environment["DOTNET_GCServer"] = "0";
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Failed to start Blob streaming fixture.");
    }

    private static async Task StopFixtureAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static async Task<(long Length, string Digest)> ReadDigestAsync(IBlobContainer container, string name, CancellationToken token)
    {
        await using var input = await container.OpenReadAsync(name, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            length += count;
            hash.AppendData(buffer.AsSpan(0, count));
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "cohesion-blob-process", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
