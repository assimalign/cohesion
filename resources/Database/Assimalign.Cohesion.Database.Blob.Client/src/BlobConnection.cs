using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Client.Internal;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client;

/// <summary>A leased connection bound to one Blob database, allowing one exchange at a time.</summary>
/// <remarks>
/// A download or listing holds the exchange until completion or disposal. Dispose returned streams
/// and enumerators before starting another operation. Disposing this connection cancels and waits
/// for its active exchange before returning the pool lease. Failed exchanges close the connection.
/// </remarks>
public sealed class BlobConnection : IAsyncDisposable
{
    private readonly DatabaseConnection _connection;
    private int _disposed;
    private int _returned;

    internal BlobConnection(DatabaseConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Gets the database selected at handshake.</summary>
    public string Database => _connection.Database;

    /// <summary>Gets whether the connection remains open and usable.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _returned) == 0 && _connection.IsOpen;

    /// <summary>Streams content and waits for the server's atomic publication acknowledgement.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="name">The ordinal, case-sensitive blob name.</param>
    /// <param name="source">A readable stream, left open and read from its current position.</param>
    /// <param name="contentType">The declared media type, or an empty string when unspecified.</param>
    /// <param name="length">The exact remaining byte count, or -1 for an unknown length.</param>
    /// <param name="overwrite">Whether an existing blob may be replaced.</param>
    /// <param name="cancellationToken">Cancellation token for source reads and the entire wire exchange.</param>
    /// <returns>The committed content length.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">The source is unreadable or the length is invalid.</exception>
    /// <exception cref="BlobClientException">The server rejects the write or the transfer fails.</exception>
    /// <exception cref="OperationCanceledException">The transfer is canceled; its connection is closed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    public ValueTask<long> UploadAsync(string container, string name, Stream source,
        string contentType = "application/octet-stream", long length = -1, bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || length < -1)
        {
            throw new ArgumentException("The source must be readable and the length must be nonnegative or -1.");
        }
        return ExecuteAsync<long>(async (reader, writer, token) =>
        {
            await WriteAsync(writer, BlobProtocolMessageType.Write,
                new BlobWriteMessage(container, name, overwrite).Encode(), token).ConfigureAwait(false);
            long sent = await BlobProtocolTransfer.SendAsync(reader, writer, source,
                new BlobTransferStartMessage(length, contentType), token).ConfigureAwait(false);
            ProtocolFrame published = await ExpectAsync(reader, token).ConfigureAwait(false);
            if (published.Type != (ProtocolMessageType)BlobProtocolMessageType.TransferComplete ||
                BlobTransferCompleteMessage.Decode(published.Payload.Span).Length != sent)
            {
                throw new ProtocolException("The server did not acknowledge the uploaded Blob's publication and length.");
            }
            return sent;
        }, cancellationToken);
    }

    /// <summary>Starts a download and returns a bounded, nonseekable readable stream.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="name">The ordinal, case-sensitive blob name.</param>
    /// <param name="cancellationToken">Cancellation token that remains active for the returned stream's lifetime.</param>
    /// <returns>A stream after its transfer metadata has been validated. The caller must dispose it.</returns>
    /// <exception cref="BlobClientException">The server rejects the read or the transfer fails before its metadata arrives.</exception>
    /// <exception cref="OperationCanceledException">The download is canceled.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    /// <remarks>
    /// Later server errors, malformed completion, and truncation throw <see cref="BlobClientException"/>
    /// from stream reads; they never produce successful EOF. Both synchronous and asynchronous reads
    /// are supported. Canceling a ReadAsync token cancels the entire transfer. Early stream disposal
    /// aborts the exchange and closes the connection; fully verified downloads allow connection reuse.
    /// </remarks>
    public async ValueTask<Stream> DownloadAsync(string container, string name, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        try
        {
            Stream stream = await _connection.ExecuteStreamingAsync(
                new BlobDownloadExchange(container, name), cancellationToken).ConfigureAwait(false);
            return new BlobDownloadStream(stream);
        }
        catch (DatabaseClientException exception)
        {
            throw new BlobClientException(exception.Code, exception.Message, exception);
        }
    }

    /// <summary>Deletes a blob.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>True if a blob was deleted; false if it did not exist.</returns>
    /// <exception cref="BlobClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    public ValueTask<bool> DeleteAsync(string container, string name, CancellationToken cancellationToken = default)
        => ExecuteAsync<bool>(async (reader, writer, token) =>
        {
            await WriteAsync(writer, BlobProtocolMessageType.Delete,
                new BlobDeleteMessage(container, name).Encode(), token).ConfigureAwait(false);
            long count = await ReadCountAsync(reader, token).ConfigureAwait(false);
            if (count > 1)
            {
                throw new ProtocolException("A Blob delete acknowledged more than one object.");
            }
            return count == 1;
        }, cancellationToken);

    /// <summary>Reads a blob's catalog metadata.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The properties, or null if the blob does not exist.</returns>
    /// <exception cref="BlobClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    public ValueTask<BlobProperties?> GetPropertiesAsync(string container, string name, CancellationToken cancellationToken = default)
        => ExecuteAsync<BlobProperties?>(async (reader, writer, token) =>
        {
            await WriteAsync(writer, BlobProtocolMessageType.GetProperties,
                new BlobGetPropertiesMessage(container, name).Encode(), token).ConfigureAwait(false);
            ProtocolFrame frame = await ExpectAsync(reader, token).ConfigureAwait(false);
            if (frame.Type == (ProtocolMessageType)BlobProtocolMessageType.OperationComplete &&
                BlobOperationCompleteMessage.Decode(frame.Payload.Span).Count == 0)
            {
                return null;
            }
            if (frame.Type != (ProtocolMessageType)BlobProtocolMessageType.Properties)
            {
                throw new ProtocolException("Expected Blob properties or an empty completion.");
            }
            BlobProperties properties = BlobPropertiesMessage.Decode(frame.Payload.Span).Properties;
            if (properties.Name != name || await ReadCountAsync(reader, token).ConfigureAwait(false) != 1)
            {
                throw new ProtocolException("The Blob properties response disagrees with its request or completion.");
            }
            return properties;
        }, cancellationToken);

    /// <summary>Enumerates blob metadata matching an ordinal prefix using bounded client buffering.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="prefix">An ordinal prefix, or null to list all blobs.</param>
    /// <param name="cancellationToken">Cancellation token for enumeration and the exchange.</param>
    /// <returns>The matching metadata. Enumeration owns the exchange until completed or disposed.</returns>
    /// <exception cref="BlobClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    public async IAsyncEnumerable<BlobProperties> GetBlobsAsync(string container, string? prefix = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var items = Channel.CreateBounded<BlobProperties>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Task worker = ListCoreAsync(items.Writer, container, prefix ?? string.Empty, operation.Token);
        try
        {
            while (await items.Reader.WaitToReadAsync(operation.Token).ConfigureAwait(false))
            {
                while (items.Reader.TryRead(out BlobProperties properties))
                {
                    yield return properties;
                }
            }
            // Completion is separate from the bounded queue so readers get the original
            // BlobClientException, never a ChannelClosedException around it.
            await worker.ConfigureAwait(false);
        }
        finally
        {
            operation.Cancel();
            try { await worker.ConfigureAwait(false); }
            catch (Exception) { /* The primary enumeration failure has already surfaced. */ }
        }
    }

    /// <summary>
    /// Returns the pool lease, after canceling and joining an active exchange. Idempotent.
    /// </summary>
    /// <returns>A task that completes when the lease is returned.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await ReturnAsync().ConfigureAwait(false);
        }
    }

    private async Task ListCoreAsync(ChannelWriter<BlobProperties> items, string container, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteCoreAsync<long>(async (reader, writer, token) =>
            {
                await WriteAsync(writer, BlobProtocolMessageType.List,
                    new BlobListMessage(container, prefix).Encode(), token).ConfigureAwait(false);
                long count = 0;
                while (true)
                {
                    ProtocolFrame frame = await ExpectAsync(reader, token).ConfigureAwait(false);
                    if (frame.Type == (ProtocolMessageType)BlobProtocolMessageType.OperationComplete)
                    {
                        if (BlobOperationCompleteMessage.Decode(frame.Payload.Span).Count != count)
                        {
                            throw new ProtocolException("The Blob listing completion count is incorrect.");
                        }
                        return count;
                    }
                    if (frame.Type != (ProtocolMessageType)BlobProtocolMessageType.Properties)
                    {
                        throw new ProtocolException("Expected a Blob listing item or completion.");
                    }
                    BlobProperties properties = BlobPropertiesMessage.Decode(frame.Payload.Span).Properties;
                    if (!properties.Name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        throw new ProtocolException("A Blob listing item does not match the requested prefix.");
                    }
                    await items.WriteAsync(properties, token).ConfigureAwait(false);
                    count = checked(count + 1);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            items.TryComplete();
        }
    }

    private ValueTask<TResult> ExecuteAsync<TResult>(
        Func<ProtocolFrameReader, ProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        EnsureOpen();
        return ExecuteCoreAsync(action, cancellationToken);
    }

    private async ValueTask<TResult> ExecuteCoreAsync<TResult>(
        Func<ProtocolFrameReader, ProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _connection.ExecuteAsync(new BlobExchange<TResult>(action), cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception)
        {
            if (!_connection.IsOpen)
            {
                await ReturnAsync().ConfigureAwait(false);
            }
            throw new BlobClientException(exception.Code, exception.Message, exception);
        }
        catch
        {
            // The shared client invalidates the incomplete exchange before returning here.
            // Return it immediately so cancellation also disconnects the server and aborts its write.
            if (!_connection.IsOpen)
            {
                await ReturnAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private void EnsureOpen()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _returned) != 0 || !_connection.IsOpen, this);

    private async ValueTask ReturnAsync()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask<long> ReadCountAsync(ProtocolFrameReader reader, CancellationToken token)
    {
        ProtocolFrame frame = await ExpectAsync(reader, token).ConfigureAwait(false);
        if (frame.Type != (ProtocolMessageType)BlobProtocolMessageType.OperationComplete)
        {
            throw new ProtocolException("Expected a Blob operation completion.");
        }
        return BlobOperationCompleteMessage.Decode(frame.Payload.Span).Count;
    }

    private static async ValueTask<ProtocolFrame> ExpectAsync(ProtocolFrameReader reader, CancellationToken token)
        => await reader.ReadFrameAsync(token).ConfigureAwait(false)
            ?? throw new ProtocolException("The server closed the connection before completing the Blob exchange.");

    private static async ValueTask WriteAsync(ProtocolFrameWriter writer, BlobProtocolMessageType type,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)type, payload), token).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
    }

    // Never certifies a failed response complete: a transfer can leave unread frames even when its
    // error code is ExecutionFailure, so every Blob failure discards the connection.
    private sealed class BlobExchange<TResult> : DatabaseProtocolExchange<TResult>
    {
        private readonly Func<ProtocolFrameReader, ProtocolFrameWriter, CancellationToken, ValueTask<TResult>> _action;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobExchange{TResult}"/> class.
        /// </summary>
        /// <param name="action">The Blob exchange body to run over the error-normalizing reader and writer.</param>
        public BlobExchange(
            Func<ProtocolFrameReader, ProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action)
            : base(BlobProtocol.Family)
        {
            _action = action;
        }

        protected override ValueTask<TResult> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
            CancellationToken cancellationToken)
            => _action(new BlobErrorReader(reader), new BlobFrameWriter(writer), cancellationToken);
    }

    private sealed class BlobErrorReader : ProtocolFrameReader
    {
        private readonly ProtocolFrameReader _reader;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobErrorReader"/> class.
        /// </summary>
        /// <param name="reader">The shared frame reader whose error frames and transport failures are normalized.</param>
        public BlobErrorReader(ProtocolFrameReader reader)
        {
            _reader = reader;
        }

        protected override async ValueTask<ProtocolFrame?> ReadFrameCoreAsync(CancellationToken cancellationToken)
        {
            ProtocolFrame? frame;
            try
            {
                frame = await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                // Retain Blob's existing transport diagnostics for materialized operations and
                // uploads. Normalize only frame I/O, never exceptions from caller-owned streams.
                throw new DatabaseClientException(ProtocolErrorCode.Internal,
                    "The connection closed while reading a Blob response.", exception);
            }
            if (frame is { Type: ProtocolMessageType.Error } errorFrame)
            {
                ProtocolErrorMessage error = ProtocolErrorMessage.Decode(errorFrame.Payload.Span);
                // BlobProtocolTransfer otherwise flattens shared errors into ProtocolException.
                // Preserve diagnostics; shared exchange completion alone determines pool health.
                throw new DatabaseClientException(error.Code, error.Message);
            }
            return frame;
        }

        // The pooled connection owns the shared reader, so the base's no-op DisposeAsyncCore stays.
    }

    private sealed class BlobFrameWriter : ProtocolFrameWriter
    {
        private readonly ProtocolFrameWriter _writer;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobFrameWriter"/> class.
        /// </summary>
        /// <param name="writer">The shared frame writer whose transport failures are normalized.</param>
        public BlobFrameWriter(ProtocolFrameWriter writer)
        {
            _writer = writer;
        }

        protected override async ValueTask WriteFrameCoreAsync(ProtocolFrame frame, CancellationToken cancellationToken)
        {
            try
            {
                await _writer.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new DatabaseClientException(ProtocolErrorCode.Internal,
                    "The connection closed while sending a Blob request.", exception);
            }
        }

        protected override async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new DatabaseClientException(ProtocolErrorCode.Internal,
                    "The connection closed while flushing a Blob request.", exception);
            }
        }

        // The pooled connection owns the shared writer, so the base's no-op DisposeAsyncCore stays.
    }

    private sealed class BlobDownloadExchange : DatabaseStreamingExchange
    {
        private readonly string _container;
        private readonly string _name;
        private BlobTransferStartMessage? _metadata;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobDownloadExchange"/> class.
        /// </summary>
        /// <param name="container">The container that holds the Blob to download.</param>
        /// <param name="name">The name of the Blob to download.</param>
        public BlobDownloadExchange(string container, string name)
            : base(BlobProtocol.Family)
        {
            _container = container;
            _name = name;
        }

        protected override async ValueTask OpenCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
            CancellationToken cancellationToken)
        {
            await WriteAsync(writer, BlobProtocolMessageType.Read,
                new BlobReadMessage(_container, _name).Encode(), cancellationToken).ConfigureAwait(false);
            ProtocolFrame start = await ExpectAsync(new BlobErrorReader(reader), cancellationToken).ConfigureAwait(false);
            if (start.Type != (ProtocolMessageType)BlobProtocolMessageType.TransferStart)
            {
                throw new ProtocolException("A Blob download must begin with TransferStart.");
            }
            _metadata = BlobTransferStartMessage.Decode(start.Payload.Span);
        }

        protected override async ValueTask CopyToCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
            Stream destination, CancellationToken cancellationToken)
            => await BlobProtocolTransfer.ReceiveAsync(new BlobErrorReader(reader), writer, destination,
                _metadata ?? throw new InvalidOperationException("The download metadata has not been read."),
                cancellationToken).ConfigureAwait(false);
    }
}
