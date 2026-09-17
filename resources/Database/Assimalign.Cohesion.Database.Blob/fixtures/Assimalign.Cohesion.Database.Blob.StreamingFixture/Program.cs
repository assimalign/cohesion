using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Blob;

namespace Assimalign.Cohesion.Database.Blob.StreamingFixture;

internal static class Program
{
    private const long LargeBlobLength = 128L * 1024 * 1024 + 123;
    private const long CommittedLength = 1024 * 1024 + 19;
    private const int BufferSize = 64 * 1024;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 2)
            {
                throw new ArgumentException("Expected mode and database root path.");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            switch (args[0])
            {
                case "roundtrip":
                    await RoundTripAsync(args[1], timeout.Token);
                    return 0;
                case "crash-writer":
                    await CrashWriterAsync(args[1], timeout.Token);
                    return 2; // Reaching this means the parent failed to kill us.
                default:
                    throw new ArgumentException("Unknown fixture mode.");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static BlobDatabaseEngine CreateEngine(string root)
        => BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions
        {
            EngineName = "blob-process-fixture",
            RootPath = root,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
            PageWriteBackBatchSize = 4096
        });

    private static async Task RoundTripAsync(string root, CancellationToken token)
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0 || available >= LargeBlobLength)
        {
            throw new InvalidOperationException($"The fixture must have less memory than the blob: available={available}, blob={LargeBlobLength}.");
        }

        string expectedHash;
        string recoveryRoot = Path.Combine(root, "recovery-image");
        await using (var engine = CreateEngine(root))
        {
            var database = (IBlobDatabase)await engine.CreateDatabaseAsync("large", token);
            var container = await database.CreateContainerAsync("objects", token);
            await using (var output = await container.OpenWriteAsync("payload", cancellationToken: token))
            {
                expectedHash = await WritePatternAsync(output, LargeBlobLength, 17, token);
            }

            await VerifyAsync(container, "payload", LargeBlobLength, expectedHash, token);
            if (engine.State != EngineState.Running)
            {
                throw new InvalidOperationException("Worker fault during large streaming write.");
            }

            // Capture committed files before disposal can checkpoint/truncate
            // the journal. No writes are active and maintenance cadences are
            // one hour. This image exercises recovery of a WAL larger than
            // available memory as well as bounded upload/download streams.
            string sourceDirectory = Path.Combine(root, "large");
            if (new FileInfo(Path.Combine(sourceDirectory, "blob.log")).Length <= LargeBlobLength)
            {
                throw new InvalidOperationException("The recovery image must retain the large object's complete journal.");
            }
            string recoveryDirectory = Path.Combine(recoveryRoot, "large");
            Directory.CreateDirectory(recoveryDirectory);
            foreach (string file in new[] { "blob.dat", "blob.log", "blob.bak" })
            {
                File.Copy(Path.Combine(sourceDirectory, file), Path.Combine(recoveryDirectory, file));
            }
        }

        // Re-open from real files under the same hard memory cap. Recovery and
        // readback must obey the bound too, not only the upload stream.
        await using (var reopened = CreateEngine(recoveryRoot))
        {
            var database = (IBlobDatabase)await reopened.OpenDatabaseAsync("large", token);
            var container = await database.GetContainerAsync("objects", token);
            await VerifyAsync(container, "payload", LargeBlobLength, expectedHash, token);
        }

        Console.WriteLine($"ROUNDTRIP_OK|{LargeBlobLength}|{available}|{expectedHash}");
    }

    private static async Task CrashWriterAsync(string root, CancellationToken token)
    {
        // This method intentionally owns undisposed streams. The parent kills
        // the process after READY so no stream, transaction, or engine cleanup
        // can turn the crash test into a graceful shutdown test.
        var engine = CreateEngine(root);
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("existing", token);
        var container = await database.CreateContainerAsync("objects", token);
        var otherDatabase = (IBlobDatabase)await engine.CreateDatabaseAsync("new-object", token);
        var otherContainer = await otherDatabase.CreateContainerAsync("objects", token);
        string committedHash;
        await using (var output = await container.OpenWriteAsync("committed", cancellationToken: token))
        {
            committedHash = await WritePatternAsync(output, CommittedLength, 17, token);
        }

        var replacement = await container.OpenWriteAsync("committed", cancellationToken: token);
        await WritePatternAsync(replacement, 256 * 1024 + 71, 203, token);
        await replacement.FlushAsync(token);
        var partialNew = await otherContainer.OpenWriteAsync("incomplete", cancellationToken: token);
        await WritePatternAsync(partialNew, 256 * 1024 + 83, 109, token);
        await partialNew.FlushAsync(token);

        // Force dirty loser pages to disk before process death. The shared
        // coordinator must retain active transaction identities in checkpoints.
        RunWorker(engine, DatabaseEngineWorkerKind.WriteAheadFlush, token);
        RunWorker(engine, DatabaseEngineWorkerKind.PageWriteBack, token);
        RunWorker(engine, DatabaseEngineWorkerKind.Checkpoint, token);
        if (engine.State != EngineState.Running)
        {
            throw new InvalidOperationException("Worker fault while preparing crash.");
        }
        Console.WriteLine($"CRASH_READY|{CommittedLength}|{committedHash}");
        await Console.Out.FlushAsync(token);
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        GC.KeepAlive(replacement);
        GC.KeepAlive(partialNew);
        GC.KeepAlive(engine);
    }

    private static void RunWorker(BlobDatabaseEngine engine, DatabaseEngineWorkerKind kind, CancellationToken token)
    {
        foreach (var worker in engine.Workers)
        {
            if (worker.Kind == kind)
            {
                ((DatabaseEngineWorker)worker).RunIteration(token);
                return;
            }
        }
        throw new InvalidOperationException($"Missing worker: {kind}.");
    }

    private static async Task<string> WritePatternAsync(Stream stream, long length, int salt, CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < length)
        {
            int count = (int)Math.Min(buffer.Length, length - offset);
            for (int index = 0; index < count; index++)
            {
                long position = offset + index;
                buffer[index] = unchecked((byte)(position * 31 + position / 251 + salt));
            }
            hash.AppendData(buffer.AsSpan(0, count));
            await stream.WriteAsync(buffer.AsMemory(0, count), token);
            offset += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task VerifyAsync(IBlobContainer container, string name, long length, string expectedHash, CancellationToken token)
    {
        var properties = await container.GetPropertiesAsync(name, token);
        if (properties?.Length != length)
        {
            throw new InvalidDataException("Blob metadata length differs from the uploaded length.");
        }
        await using var input = await container.OpenReadAsync(name, token);
        var buffer = new byte[BufferSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long actualLength = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            actualLength += count;
            hash.AppendData(buffer.AsSpan(0, count));
        }
        if (actualLength != length || Convert.ToHexString(hash.GetHashAndReset()) != expectedHash)
        {
            throw new InvalidDataException("Streamed blob bytes differ from the uploaded bytes.");
        }
    }
}
