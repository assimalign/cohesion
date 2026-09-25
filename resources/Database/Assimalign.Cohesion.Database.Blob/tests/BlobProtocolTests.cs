using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobProtocolTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Blob transfer: Streams beyond one frame with bounded content in flight")]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Transfer_LargerThanFrame_ShouldStreamBothDirectionsWithoutWholeObjectBuffers(bool upload, bool knownLength)
    {
        // Neither fixture has a backing payload array or permits seeking. The transport deliberately
        // has no pipe backpressure, so the observed bound depends on the Blob acknowledgement flow.
        const long length = ProtocolFrameHeader.MaxPayloadLength + 173;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = cancellation.Token;
        var pair = InMemoryConnectionPair.Create();
        await using var clientConnection = pair.Client;
        await using var serverConnection = pair.Server;
        await using var client = new ProtocolChannel(clientConnection.AsStream(), BlobProtocol.Family);
        await using var server = new ProtocolChannel(serverConnection.AsStream(), BlobProtocol.Family);
        await using var destination = new VerifyingStream();
        await using var source = new GeneratedStream(length, destination);
        var metadata = new BlobTransferStartMessage(knownLength ? length : -1, "application/test");

        Task serverTask = ServeAsync();
        await WriteAsync(client, ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "objects", "tester").Encode(), token);
        (await ReadAsync(client, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(client, ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty, token);
        (await ReadAsync(client, token)).Type.ShouldBe(ProtocolMessageType.Ready);
        if (upload)
        {
            await WriteAsync(client, (ProtocolMessageType)BlobProtocolMessageType.Write,
                new BlobWriteMessage("files", "large.bin", false).Encode(), token);
            (await BlobProtocolTransfer.SendAsync(client, source, metadata, token)).ShouldBe(length);
            ProtocolFrame acknowledgement = await ReadAsync(client, token);
            acknowledgement.Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.TransferComplete);
            BlobTransferCompleteMessage.Decode(acknowledgement.Payload.Span).Length.ShouldBe(length);
        }
        else
        {
            await WriteAsync(client, (ProtocolMessageType)BlobProtocolMessageType.Read,
                new BlobReadMessage("files", "large.bin").Encode(), token);
            (await BlobProtocolTransfer.ReceiveAsync(client, destination, token)).ShouldBe(new(length, "application/test"));
        }
        await serverTask;

        source.BytesRead.ShouldBe(length);
        destination.BytesWritten.ShouldBe(length);
        source.LargestReadRequest.ShouldBe(BlobProtocol.MaxChunkLength);
        source.LargestUnacceptedContent.ShouldBeLessThanOrEqualTo(BlobProtocol.MaxChunkLength);
        destination.LargestWrite.ShouldBeLessThanOrEqualTo(BlobProtocol.MaxChunkLength);
        destination.WriteCount.ShouldBeGreaterThan(256);

        async Task ServeAsync()
        {
            ProtocolFrame startupFrame = await ReadAsync(server, token);
            startupFrame.Type.ShouldBe(ProtocolMessageType.Startup);
            ProtocolStartupMessage startup = ProtocolStartupMessage.Decode(startupFrame.Payload.Span);
            startup.Database.ShouldBe("objects");
            ProtocolVersion.TryNegotiate(startup.Version, out ProtocolVersion negotiated).ShouldBeTrue();
            negotiated.ShouldBe(ProtocolVersion.Current);
            await WriteAsync(server, ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, token);
            (await ReadAsync(server, token)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
            await WriteAsync(server, ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty, token);
            ProtocolFrame request = await ReadAsync(server, token);
            if (upload)
            {
                request.Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.Write);
                BlobWriteMessage.Decode(request.Payload.Span).ShouldBe(new("files", "large.bin", false));
                (await BlobProtocolTransfer.ReceiveAsync(server, destination, token)).ShouldBe(new(length, "application/test"));
                await WriteAsync(server, (ProtocolMessageType)BlobProtocolMessageType.TransferComplete,
                    new BlobTransferCompleteMessage(length).Encode(), token);
            }
            else
            {
                request.Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.Read);
                BlobReadMessage.Decode(request.Payload.Span).ShouldBe(new("files", "large.bin"));
                (await BlobProtocolTransfer.SendAsync(server, source, metadata, token)).ShouldBe(length);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Blob transfer: Empty streams complete without chunks")]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Transfer_EmptyContent_ShouldCompleteWithoutChunks(long declaredLength)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = InMemoryConnectionPair.Create();
        await using var clientConnection = pair.Client;
        await using var serverConnection = pair.Server;
        await using var client = new ProtocolChannel(clientConnection.AsStream(), BlobProtocol.Family);
        await using var server = new ProtocolChannel(serverConnection.AsStream(), BlobProtocol.Family);
        using var source = new MemoryStream();
        using var destination = new VerifyingStream();

        Task<BlobTransferStartMessage> receive = BlobProtocolTransfer.ReceiveAsync(server, destination, cancellation.Token).AsTask();
        (await BlobProtocolTransfer.SendAsync(client, source, new(declaredLength), cancellation.Token)).ShouldBe(0);
        (await receive).Length.ShouldBe(0);
        destination.WriteCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob messages: Encodings match independent wire vectors")]
    public void Encode_WireVectors_ShouldUseBigEndianAndStrictUtf8()
    {
        new BlobReadMessage("a", "β").Encode().ShouldBe(new byte[] { 0, 0, 0, 1, 97, 0, 0, 0, 2, 206, 178 });
        new BlobWriteMessage("a", "b", false).Encode().ShouldBe(new byte[] { 0, 0, 0, 1, 97, 0, 0, 0, 1, 98, 0 });
        new BlobTransferStartMessage(-1, "").Encode().ShouldBe(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0 });
        new BlobTransferCompleteMessage(65_537).Encode().ShouldBe(new byte[] { 0, 0, 0, 0, 0, 1, 0, 1 });
        new BlobChunkAcknowledgementMessage(65_537).Encode().ShouldBe(new byte[] { 0, 0, 0, 0, 0, 1, 0, 1 });
        var chunk = new BlobChunkMessage(new byte[] { 0, 1, 255 });
        chunk.ToFrame().Type.ShouldBe((ProtocolMessageType)67);
        chunk.ToFrame().Payload.ToArray().ShouldBe(new byte[] { 0, 1, 255 });
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob messages: Malformed payloads fail before use")]
    public void Decode_MalformedPayloads_ShouldRejectInvalidLengthsUtf8AndTrailingBytes()
    {
        Should.Throw<ProtocolException>(() => BlobReadMessage.Decode(new byte[] { 127, 255, 255, 255 }));
        Should.Throw<ProtocolException>(() => BlobReadMessage.Decode(new byte[] { 0, 0, 0, 1, 255 }));
        Should.Throw<ProtocolException>(() => BlobWriteMessage.Decode(new byte[] { 0, 0, 0, 1, 97, 0, 0, 0, 1, 98, 2 }));
        Should.Throw<ProtocolException>(() => BlobTransferStartMessage.Decode(new byte[] { 255, 255, 255, 255, 255, 255, 255, 254, 0, 0, 0, 0 }));
        Should.Throw<ProtocolException>(() => BlobTransferCompleteMessage.Decode(new byte[9]));
        Should.Throw<ProtocolException>(() => BlobChunkMessage.Decode(ReadOnlyMemory<byte>.Empty));
        Should.Throw<ProtocolException>(() => BlobChunkMessage.Decode(new byte[BlobProtocol.MaxChunkLength + 1]));
        Should.Throw<ProtocolException>(() => new BlobReadMessage("a", "\uD800").Encode());
        Should.Throw<ProtocolException>(() => new BlobReadMessage("a", new string('b', 65_536)).Encode());
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob receive: Completion count must match received content")]
    public async Task Receive_MismatchedCompletion_ShouldRejectTransfer()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = cancellation.Token;
        var pair = InMemoryConnectionPair.Create();
        await using var clientConnection = pair.Client;
        await using var serverConnection = pair.Server;
        await using var client = new ProtocolChannel(clientConnection.AsStream(), BlobProtocol.Family);
        await using var server = new ProtocolChannel(serverConnection.AsStream(), BlobProtocol.Family);
        using var destination = new MemoryStream();
        Task<BlobTransferStartMessage> receive = BlobProtocolTransfer.ReceiveAsync(server, destination, token).AsTask();

        await WriteAsync(client, (ProtocolMessageType)BlobProtocolMessageType.TransferStart, new BlobTransferStartMessage().Encode(), token);
        await WriteAsync(client, (ProtocolMessageType)BlobProtocolMessageType.Chunk, new byte[] { 42 }, token);
        ProtocolFrame accepted = await ReadAsync(client, token);
        accepted.Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.ChunkAcknowledgement);
        BlobChunkAcknowledgementMessage.Decode(accepted.Payload.Span).Length.ShouldBe(1);
        await WriteAsync(client, (ProtocolMessageType)BlobProtocolMessageType.TransferComplete, new BlobTransferCompleteMessage(2).Encode(), token);

        await Should.ThrowAsync<ProtocolException>(async () => await receive);
        destination.Length.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob send: Cancellation interrupts chunk acknowledgement waits")]
    public async Task Send_CanceledWhileAwaitingAcknowledgement_ShouldStopReadingSource()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = InMemoryConnectionPair.Create();
        await using var clientConnection = pair.Client;
        await using var serverConnection = pair.Server;
        await using var client = new ProtocolChannel(clientConnection.AsStream(), BlobProtocol.Family);
        await using var server = new ProtocolChannel(serverConnection.AsStream(), BlobProtocol.Family);
        using var destination = new VerifyingStream();
        using var source = new GeneratedStream(2 * BlobProtocol.MaxChunkLength, destination);
        Task<long> send = BlobProtocolTransfer.SendAsync(client, source, new(), cancellation.Token).AsTask();
        (await ReadAsync(server, cancellation.Token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.TransferStart);
        (await ReadAsync(server, cancellation.Token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.Chunk);

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await send);
        source.BytesRead.ShouldBeLessThanOrEqualTo(BlobProtocol.MaxChunkLength);
    }

    private static async Task WriteAsync(ProtocolChannel channel, ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
    {
        ProtocolFrame? frame = await channel.Reader.ReadFrameAsync(token);
        frame.ShouldNotBeNull();
        return frame.Value;
    }

    private sealed class GeneratedStream : Stream
    {
        private readonly long _length;
        private readonly VerifyingStream _destination;
        /// <summary>
        /// Initializes a new instance of the <see cref="GeneratedStream"/> class.
        /// </summary>
        /// <param name="length">The total number of bytes the stream generates.</param>
        /// <param name="destination">The stream whose written byte count bounds the unaccepted content.</param>
        public GeneratedStream(long length, VerifyingStream destination)
        {
            _length = length;
            _destination = destination;
        }
        internal long BytesRead { get; private set; }
        internal long LargestUnacceptedContent { get; private set; }
        internal int LargestReadRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestReadRequest = Math.Max(LargestReadRequest, buffer.Length);
            // Deliberately exercise short reads; a chunk need not fill the negotiated maximum.
            int count = (int)Math.Min(Math.Min(buffer.Length, 49_157), _length - BytesRead);
            for (int index = 0; index < count; index++)
            {
                buffer.Span[index] = ContentByte(BytesRead + index);
            }
            BytesRead += count;
            LargestUnacceptedContent = Math.Max(LargestUnacceptedContent, BytesRead - _destination.BytesWritten);
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class VerifyingStream : Stream
    {
        private long _bytesWritten;
        internal long BytesWritten => Interlocked.Read(ref _bytesWritten);
        internal int LargestWrite { get; private set; }
        internal int WriteCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long position = BytesWritten;
            for (int index = 0; index < buffer.Length; index++)
            {
                if (buffer.Span[index] != ContentByte(position + index))
                {
                    throw new InvalidDataException($"Incorrect content at {position + index}.");
                }
            }
            Interlocked.Add(ref _bytesWritten, buffer.Length);
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            WriteCount++;
            return ValueTask.CompletedTask;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte ContentByte(long offset) => (byte)((offset * 31 + offset / 251) & 255);
}
