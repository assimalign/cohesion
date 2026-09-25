using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client.Internal;

internal sealed class BlobConnection : IBlobConnection
{
    private readonly IDatabaseConnection _connection;
    private int _disposed;
    private int _returned;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobConnection"/> class.
    /// </summary>
    /// <param name="connection">The rented shared database connection that carries the Blob exchanges.</param>
    public BlobConnection(IDatabaseConnection connection)
    {
        _connection = connection;
    }

    public string Database => _connection.Database;
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _returned) == 0 && _connection.IsOpen;

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
        Func<IProtocolFrameReader, IProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        EnsureOpen();
        return ExecuteCoreAsync(action, cancellationToken);
    }

    private async ValueTask<TResult> ExecuteCoreAsync<TResult>(
        Func<IProtocolFrameReader, IProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action,
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

    private static async ValueTask<long> ReadCountAsync(IProtocolFrameReader reader, CancellationToken token)
    {
        ProtocolFrame frame = await ExpectAsync(reader, token).ConfigureAwait(false);
        if (frame.Type != (ProtocolMessageType)BlobProtocolMessageType.OperationComplete)
        {
            throw new ProtocolException("Expected a Blob operation completion.");
        }
        return BlobOperationCompleteMessage.Decode(frame.Payload.Span).Count;
    }

    private static async ValueTask<ProtocolFrame> ExpectAsync(IProtocolFrameReader reader, CancellationToken token)
        => await reader.ReadFrameAsync(token).ConfigureAwait(false)
            ?? throw new ProtocolException("The server closed the connection before completing the Blob exchange.");

    private static async ValueTask WriteAsync(IProtocolFrameWriter writer, BlobProtocolMessageType type,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)type, payload), token).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
    }

    private sealed class BlobExchange<TResult> : IDatabaseProtocolExchange<TResult>
    {
        private readonly Func<IProtocolFrameReader, IProtocolFrameWriter, CancellationToken, ValueTask<TResult>> _action;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobExchange{TResult}"/> class.
        /// </summary>
        /// <param name="action">The Blob exchange body to run over the error-normalizing reader and writer.</param>
        public BlobExchange(
            Func<IProtocolFrameReader, IProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action)
        {
            _action = action;
        }

        public ProtocolMessageFamily Family => BlobProtocol.Family;

        public ValueTask<TResult> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer,
            CancellationToken cancellationToken = default)
            => _action(new BlobErrorReader(reader), new BlobFrameWriter(writer), cancellationToken);
    }

    private sealed class BlobErrorReader : IProtocolFrameReader
    {
        private readonly IProtocolFrameReader _reader;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobErrorReader"/> class.
        /// </summary>
        /// <param name="reader">The shared frame reader whose error frames and transport failures are normalized.</param>
        public BlobErrorReader(IProtocolFrameReader reader)
        {
            _reader = reader;
        }

        public async ValueTask<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlobFrameWriter : IProtocolFrameWriter
    {
        private readonly IProtocolFrameWriter _writer;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobFrameWriter"/> class.
        /// </summary>
        /// <param name="writer">The shared frame writer whose transport failures are normalized.</param>
        public BlobFrameWriter(IProtocolFrameWriter writer)
        {
            _writer = writer;
        }

        public async ValueTask WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken = default)
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

        public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlobDownloadExchange : IDatabaseStreamingExchange
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
        {
            _container = container;
            _name = name;
        }

        public ProtocolMessageFamily Family => BlobProtocol.Family;

        public async ValueTask OpenAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer,
            CancellationToken cancellationToken = default)
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

        public async ValueTask CopyToAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer,
            Stream destination, CancellationToken cancellationToken = default)
            => await BlobProtocolTransfer.ReceiveAsync(new BlobErrorReader(reader), writer, destination,
                _metadata ?? throw new InvalidOperationException("The download metadata has not been read."),
                cancellationToken).ConfigureAwait(false);
    }
}
