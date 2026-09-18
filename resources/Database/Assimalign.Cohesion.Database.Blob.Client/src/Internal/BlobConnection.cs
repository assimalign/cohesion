using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client;

internal sealed class BlobConnection(IDatabaseConnection connection) : IBlobConnection
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private int _disposed;
    private int _returned;

    public string Database => connection.Database;
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _returned) == 0 && connection.IsOpen;

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
        EnterExchange();
        var stream = new BlobDownloadStream(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token));
        stream.SetCompletion(DownloadCoreAsync(stream, container, name));
        try
        {
            await stream.Started.ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
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
        EnterExchange();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _exchange.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReturnAsync().ConfigureAwait(false);
        }
        finally
        {
            _exchange.Release();
            _lifetime.Dispose();
        }
    }

    private async Task DownloadCoreAsync(BlobDownloadStream stream, string container, string name)
    {
        Exception? failure = null;
        try
        {
            await ExecuteCoreAsync<long>(async (reader, writer, token) =>
            {
                await WriteAsync(writer, BlobProtocolMessageType.Read,
                    new BlobReadMessage(container, name).Encode(), token).ConfigureAwait(false);
                ProtocolFrame start = await ExpectAsync(reader, token).ConfigureAwait(false);
                if (start.Type != (ProtocolMessageType)BlobProtocolMessageType.TransferStart)
                {
                    throw new ProtocolException("A Blob download must begin with TransferStart.");
                }
                BlobTransferStartMessage metadata = BlobTransferStartMessage.Decode(start.Payload.Span);
                stream.SignalStarted();
                BlobTransferStartMessage completed = await BlobProtocolTransfer.ReceiveAsync(
                    reader, writer, stream.Destination, metadata, token).ConfigureAwait(false);
                return completed.Length;
            }, stream.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _exchange.Release();
            stream.Complete(failure);
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
            _exchange.Release();
            items.TryComplete();
        }
    }

    private async ValueTask<TResult> ExecuteAsync<TResult>(
        Func<IProtocolFrameReader, IProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        EnterExchange();
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            return await ExecuteCoreAsync(action, operation.Token).ConfigureAwait(false);
        }
        finally
        {
            _exchange.Release();
        }
    }

    private async ValueTask<TResult> ExecuteCoreAsync<TResult>(
        Func<IProtocolFrameReader, IProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await connection.ExecuteAsync(new BlobExchange<TResult>(action), cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception)
        {
            await ReturnAsync().ConfigureAwait(false);
            throw new BlobClientException(exception.Code, exception.Message, exception);
        }
        catch
        {
            // The shared client invalidates the incomplete exchange before returning here.
            // Return it immediately so cancellation also disconnects the server and aborts its write.
            await ReturnAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void EnterExchange()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _returned) != 0, this);
        if (!_exchange.Wait(0))
        {
            throw new InvalidOperationException("A Blob exchange is already active on this connection.");
        }
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _returned) != 0)
        {
            _exchange.Release();
            throw new ObjectDisposedException(nameof(BlobConnection));
        }
    }

    private async ValueTask ReturnAsync()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
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

    private sealed class BlobExchange<TResult>(
        Func<IProtocolFrameReader, IProtocolFrameWriter, CancellationToken, ValueTask<TResult>> action)
        : IDatabaseProtocolExchange<TResult>
    {
        public ProtocolMessageFamily Family => BlobProtocol.Family;

        public ValueTask<TResult> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer,
            CancellationToken cancellationToken = default)
            => action(new BlobErrorReader(reader), new BlobFrameWriter(writer), cancellationToken);
    }

    private sealed class BlobErrorReader(IProtocolFrameReader reader) : IProtocolFrameReader
    {
        public async ValueTask<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            ProtocolFrame? frame;
            try
            {
                frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                // A transport may expose its completed pipe as InvalidOperationException
                // rather than IOException. Translate only frame I/O, never caller stream errors.
                throw new BlobClientException(ProtocolErrorCode.Internal,
                    "The connection closed while reading a Blob response.", exception);
            }
            if (frame is { Type: ProtocolMessageType.Error } errorFrame)
            {
                ProtocolErrorMessage error = ProtocolErrorMessage.Decode(errorFrame.Payload.Span);
                // A model exception makes the shared pool discard every failed Blob exchange,
                // including ExecutionFailure, which is only reusable for completed SQL-like commands.
                throw new BlobClientException(error.Code, error.Message);
            }
            return frame;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlobFrameWriter(IProtocolFrameWriter writer) : IProtocolFrameWriter
    {
        public async ValueTask WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken = default)
        {
            try
            {
                await writer.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new BlobClientException(ProtocolErrorCode.Internal,
                    "The connection closed while sending a Blob request.", exception);
            }
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new BlobClientException(ProtocolErrorCode.Internal,
                    "The connection closed while flushing a Blob request.", exception);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
