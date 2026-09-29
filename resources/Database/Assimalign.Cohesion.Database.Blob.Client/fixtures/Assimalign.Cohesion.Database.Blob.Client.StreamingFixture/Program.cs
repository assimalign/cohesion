using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client.StreamingFixture;

internal static class Program
{
    private const long BlobLength = 256L * 1024 * 1024 + 123;
    private const long MaximumHeap = 64L * 1024 * 1024;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 1)
            {
                throw new ArgumentException("Expected one database root path.");
            }
            long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available <= 0 || available > MaximumHeap || BlobLength < available * 4)
            {
                throw new InvalidOperationException($"The wire fixture needs a heap at most 64 MiB and a blob at least four times larger: heap={available}, blob={BlobLength}.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            CancellationToken token = timeout.Token;
            await using var engine = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions
            {
                RootPath = args[0],
                EngineName = "bounded-heap-blob-wire",
                CheckpointInterval = TimeSpan.FromHours(1),
                PageWriteBackInterval = TimeSpan.FromHours(1),
                MaintenanceInterval = TimeSpan.FromHours(1),
                PageWriteBackBatchSize = 4096
            });
            var database = (IBlobDatabase)await engine.CreateDatabaseAsync("large", token);
            await database.CreateContainerAsync("objects", token);
            await using var listener = new InMemoryConnectionListener();
            await using var server = BlobDatabaseServer.Create(engine, new BlobDatabaseServerOptions { Listener = listener });
            await server.StartAsync(token);
            await using var client = BlobClient.Create(new BlobClientOptions
            {
                Settings = new DatabaseConnectionSettings { Database = "large", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
                ConnectionFactory = listener.CreateFactory()
            });
            await using var connection = await client.ConnectAsync(token);

            // The expected digest is generated independently of the transfer.
            // Both loops have one small buffer and no backing payload array/file.
            string expectedDigest = ExpectedDigest();
            using var source = new PatternStream();
            long sent = await connection.UploadAsync("objects", "payload", source, "application/test-pattern",
                length: BlobLength, cancellationToken: token);
            if (sent != BlobLength || source.LargestReadRequest > BlobProtocol.MaxChunkLength)
            {
                throw new InvalidDataException("Upload did not preserve the length or bounded source read size.");
            }
            var properties = await connection.GetPropertiesAsync("objects", "payload", token);
            if (properties?.Length != BlobLength || properties?.ContentType != "application/test-pattern")
            {
                throw new InvalidDataException("Committed wire metadata differs from the uploaded object.");
            }
            await using var download = await connection.DownloadAsync("objects", "payload", token);
            var buffer = new byte[64 * 1024];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            int count;
            while ((count = await download.ReadAsync(buffer, token)) != 0)
            {
                hash.AppendData(buffer.AsSpan(0, count));
                received += count;
            }
            string digest = Convert.ToHexString(hash.GetHashAndReset());
            if (received != BlobLength || digest != expectedDigest)
            {
                throw new InvalidDataException($"Wire content changed: received={received}, digest={digest}, expected={expectedDigest}.");
            }
            if (engine.State != EngineState.Running)
            {
                throw new InvalidOperationException("Engine worker faulted during bounded-memory transfer.");
            }
            Console.WriteLine($"WIRE_ROUNDTRIP_OK|{received}|{available}|{digest}|{source.LargestReadRequest}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string ExpectedDigest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[32 * 1024];
        for (long offset = 0; offset < BlobLength;)
        {
            int count = (int)Math.Min(buffer.Length, BlobLength - offset);
            for (int index = 0; index < count; index++)
            {
                long position = offset + index;
                buffer[index] = unchecked((byte)(position * 31 + position / 251 + 17));
            }
            hash.AppendData(buffer.AsSpan(0, count));
            offset += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class PatternStream : Stream
    {
        private long _offset;
        internal int LargestReadRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestReadRequest = Math.Max(LargestReadRequest, buffer.Length);
            int count = (int)Math.Min(buffer.Length, BlobLength - _offset);
            for (int index = 0; index < count; index++)
            {
                long position = _offset + index;
                buffer.Span[index] = unchecked((byte)(position * 31 + position / 251 + 17));
            }
            _offset += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
