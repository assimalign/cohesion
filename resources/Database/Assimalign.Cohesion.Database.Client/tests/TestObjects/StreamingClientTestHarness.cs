using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>A second model with its own wire family and no Blob code or stream lifetime machinery.</summary>
internal sealed class StreamingClientTestHarness : IAsyncDisposable
{
    internal const int ChunkLength = 16 * 1024;
    private const ProtocolMessageType request = (ProtocolMessageType)64;
    private const ProtocolMessageType header = (ProtocolMessageType)65;
    private const ProtocolMessageType content = (ProtocolMessageType)66;
    private const ProtocolMessageType complete = (ProtocolMessageType)67;
    internal static readonly ProtocolMessageFamily Family = new("test-document-transfer", 64, 65, 66, 67);

    private readonly InMemoryConnectionListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(30));
    private readonly List<Task> _sessions = [];
    private readonly Task _accepting;
    private readonly string _response;
    private int _acceptedConnections;

    internal StreamingClientTestHarness(string response = "complete")
    {
        _response = response;
        Payload = new byte[48 * ChunkLength];
        for (int index = 0; index < Payload.Length; index++)
        {
            Payload[index] = (byte)(index % 251);
        }

        Client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "documents", Principal = "tester", EndPoint = _listener.EndPoint, MaxPoolSize = 1,
            },
            ConnectionFactory = _listener.CreateFactory(),
            Family = Family,
        });
        _accepting = AcceptAsync();
    }

    internal IDatabaseClient Client { get; }
    internal byte[] Payload { get; }
    internal int AcceptedConnections => Volatile.Read(ref _acceptedConnections);
    internal TaskCompletionSource ContinueResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource FirstChunkSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource TransferSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static IDatabaseStreamingExchange CreateExchange() => new DocumentExchange();
    internal static IDatabaseStreamingExchange CreateUntokenedExchange(bool synchronous,
        TaskCompletionSource writeStarted, TaskCompletionSource writeCompleted)
        => new DocumentExchange(synchronous, writeStarted, writeCompleted);
    internal static IDatabaseProtocolExchange<ProtocolMessageType> CreatePing() => new PingExchange();

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await Client.DisposeAsync();
        await _listener.DisposeAsync();
        await _accepting;
        await Task.WhenAll(_sessions);
        _lifetime.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var transport = await _listener.AcceptAsync(_lifetime.Token);
                Interlocked.Increment(ref _acceptedConnections);
                _sessions.Add(ServeAsync(transport, _lifetime.Token));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task ServeAsync(IConnection transport, CancellationToken cancellationToken)
    {
        await using (transport)
        await using (var channel = new ProtocolChannel(transport.AsStream(), Family))
        {
            try
            {
                (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.Startup);
                await WriteAsync(channel.Writer, ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, cancellationToken);
                (await ReadAsync(channel.Reader, cancellationToken)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
                await WriteAsync(channel.Writer, ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty, cancellationToken);
                while (await channel.Reader.ReadFrameAsync(cancellationToken) is { } frame)
                {
                    if (frame.Type == ProtocolMessageType.Terminate)
                    {
                        return;
                    }
                    if (frame.Type == ProtocolMessageType.Ping)
                    {
                        await WriteAsync(channel.Writer, ProtocolMessageType.Pong, ReadOnlyMemory<byte>.Empty, cancellationToken);
                        continue;
                    }

                    frame.Type.ShouldBe(request);
                    if (_response == "header-error")
                    {
                        await WriteErrorAsync(channel.Writer, cancellationToken);
                        continue;
                    }

                    await WriteAsync(channel.Writer, header, EncodeLength(Payload.Length), cancellationToken);
                    int firstChunkLength = _response == "large-frame" ? Payload.Length : ChunkLength;
                    await WriteAsync(channel.Writer, content, Payload.AsMemory(0, firstChunkLength), cancellationToken);
                    FirstChunkSent.TrySetResult();
                    await ContinueResponse.Task.WaitAsync(cancellationToken);

                    if (_response == "error")
                    {
                        await WriteErrorAsync(channel.Writer, cancellationToken);
                        continue;
                    }
                    if (_response == "truncated")
                    {
                        return;
                    }
                    if (_response != "wrong-length")
                    {
                        for (int offset = firstChunkLength; offset < Payload.Length; offset += ChunkLength)
                        {
                            await WriteAsync(channel.Writer, content, Payload.AsMemory(offset, ChunkLength), cancellationToken);
                        }
                    }
                    await WriteAsync(channel.Writer, complete, EncodeLength(Payload.Length), cancellationToken);
                    TransferSent.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
            {
                // The abort tests intentionally close a transport with an unfinished response.
            }
        }
    }

    private static byte[] EncodeLength(int length)
    {
        byte[] encoded = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(encoded, length);
        return encoded;
    }

    private static ValueTask WriteErrorAsync(IProtocolFrameWriter writer, CancellationToken cancellationToken)
        => WriteAsync(writer, ProtocolMessageType.Error,
            new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "Document transfer failed after a storage error.").Encode(), cancellationToken);

    private static async ValueTask WriteAsync(IProtocolFrameWriter writer, ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new ProtocolFrame(type, payload), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(IProtocolFrameReader reader, CancellationToken cancellationToken)
    {
        var frame = await reader.ReadFrameAsync(cancellationToken)
            ?? throw new ProtocolException("Document transfer ended before its completion frame.");
        if (frame.Type == ProtocolMessageType.Error)
        {
            var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
            throw new DatabaseClientException(error.Code, error.Message);
        }
        return frame;
    }

    private sealed class DocumentExchange : IDatabaseStreamingExchange
    {
        private readonly bool? _synchronousUntokenedWrite;
        private readonly TaskCompletionSource? _writeStarted;
        private readonly TaskCompletionSource? _writeCompleted;
        private int _length;

        /// <summary>Initializes a new instance of the <see cref="DocumentExchange"/> class.</summary>
        /// <param name="synchronousUntokenedWrite">
        /// <see langword="true"/> to copy each content chunk with a synchronous write, <see langword="false"/> to copy it
        /// with an asynchronous write that omits the cancellation token, or <see langword="null"/> to pass the token through.
        /// </param>
        /// <param name="writeStarted">Signaled when a content chunk write starts, or <see langword="null"/>.</param>
        /// <param name="writeCompleted">Signaled when a content chunk write completes, or <see langword="null"/>.</param>
        public DocumentExchange(bool? synchronousUntokenedWrite = null,
            TaskCompletionSource? writeStarted = null, TaskCompletionSource? writeCompleted = null)
        {
            _synchronousUntokenedWrite = synchronousUntokenedWrite;
            _writeStarted = writeStarted;
            _writeCompleted = writeCompleted;
        }

        public ProtocolMessageFamily Family => StreamingClientTestHarness.Family;

        public async ValueTask OpenAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
        {
            await WriteAsync(writer, request, ReadOnlyMemory<byte>.Empty, cancellationToken);
            var frame = await ReadAsync(reader, cancellationToken);
            if (frame.Type != header || frame.Payload.Length != sizeof(int))
            {
                throw new ProtocolException("Expected document transfer metadata.");
            }
            _length = BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.Span);
        }

        public async ValueTask CopyToAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, Stream destination, CancellationToken cancellationToken = default)
        {
            int received = 0;
            while (true)
            {
                var frame = await ReadAsync(reader, cancellationToken);
                if (frame.Type == content)
                {
                    received += frame.Payload.Length;
                    _writeStarted?.TrySetResult();
                    if (_synchronousUntokenedWrite == true)
                    {
                        destination.Write(frame.Payload.Span);
                    }
                    else if (_synchronousUntokenedWrite == false)
                    {
                        await destination.WriteAsync(frame.Payload);
                    }
                    else
                    {
                        await destination.WriteAsync(frame.Payload, cancellationToken);
                    }
                    _writeCompleted?.TrySetResult();
                    continue;
                }
                if (frame.Type != complete || frame.Payload.Length != sizeof(int) ||
                    BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.Span) != received || received != _length)
                {
                    throw new ProtocolException("Document completion did not verify the received content.");
                }
                return;
            }
        }
    }

    private sealed class PingExchange : IDatabaseProtocolExchange<ProtocolMessageType>
    {
        public ProtocolMessageFamily Family => StreamingClientTestHarness.Family;

        public async ValueTask<ProtocolMessageType> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
        {
            await WriteAsync(writer, ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty, cancellationToken);
            return (await ReadAsync(reader, cancellationToken)).Type;
        }
    }
}
