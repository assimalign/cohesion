using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol.Internal;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

[Collection(nameof(ProtocolEventSourceCollection))]
public sealed class ProtocolBatchTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Ordinary row writers leave batch and borrow counters unchanged")]
    public async Task WriteFrameAsync_OrdinaryWriter_ShouldAvoidProcessWideCounters()
    {
        // Arrange
        using var stream = new RecordingWriteStream();
        long writes = ProtocolEventSource.Log.TransportWrites;
        long flushes = ProtocolEventSource.Log.TransportFlushes;
        long rented = ProtocolEventSource.Log.RentedBuffers;
        long returned = ProtocolEventSource.Log.ReturnedBuffers;
        long inUse = ProtocolEventSource.Log.PooledBuffersInUse;
        await using var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);

        // Act: SQL writes a payload frame per row; empty frames and explicit flushes also stay uncounted.
        for (int i = 0; i < 20; i++)
        {
            await writer.WriteFrameAsync(new((ProtocolMessageType)7, new byte[] { 1, 2, 3 }), CancellationToken.None);
        }
        await writer.WriteFrameAsync(new(ProtocolMessageType.Pong, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        await writer.FlushAsync(CancellationToken.None);

        // Assert
        stream.WriteLengths.Count.ShouldBe(21);
        ProtocolEventSource.Log.TransportWrites.ShouldBe(writes);
        ProtocolEventSource.Log.TransportFlushes.ShouldBe(flushes);
        ProtocolEventSource.Log.RentedBuffers.ShouldBe(rented);
        ProtocolEventSource.Log.ReturnedBuffers.ShouldBe(returned);
        ProtocolEventSource.Log.PooledBuffersInUse.ShouldBe(inUse);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Twenty frames use one bounded transport write")]
    public async Task WriteFrameAsync_TwentyFrames_ShouldCopyOnceAndFlushOneBatch()
    {
        // Arrange
        using var stream = new RecordingWriteStream();
        long writes = ProtocolEventSource.Log.TransportWrites;
        long flushes = ProtocolEventSource.Log.TransportFlushes;
        await using var writer = ProtocolFrameWriter.CreateBatched(stream, leaveOpen: true);
        byte[] payload = [1, 2, 3];

        // Act
        for (int i = 0; i < 20; i++) { await writer.WriteFrameAsync(new(ProtocolMessageType.Error, payload), CancellationToken.None); }
        Array.Fill(payload, (byte)9);
        stream.WriteLengths.ShouldBeEmpty();
        await writer.FlushAsync(CancellationToken.None);

        // Assert: a flush-on-write pipe performs one implicit plus one explicit flush here.
        stream.WriteLengths.ShouldBe([20 * (ProtocolFrameHeader.Size + 3)]);
        (ProtocolEventSource.Log.TransportWrites - writes).ShouldBe(1);
        (ProtocolEventSource.Log.TransportFlushes - flushes).ShouldBe(1);
        stream.Position = 0;
        await using var reader = ProtocolFrameReader.Create(stream, leaveOpen: true);
        for (int i = 0; i < 20; i++)
        {
            var frame = (await reader.ReadBorrowedFrameAsync(CancellationToken.None)).ShouldNotBeNull();
            frame.Payload.ToArray().ShouldBe([1, 2, 3]);
        }
        (await reader.ReadBorrowedFrameAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - Count and byte bounds preserve frame order")]
    [InlineData(1000, 4, 7, 10, 3)]
    [InlineData(30, 32, 7, 10, 5)]
    [InlineData(10, 32, 40, 3, 3)]
    public async Task WriteFrameAsync_BatchBounds_ShouldSendCompleteFrames(int bytes, int frames, int payloadSize, int count, int expectedWrites)
    {
        // Arrange
        using var stream = new RecordingWriteStream();
        await using var writer = ProtocolFrameWriter.CreateBatched(stream, true, bytes, frames);

        // Act
        for (int i = 0; i < count; i++)
        {
            byte[] payload = new byte[payloadSize];
            payload[0] = (byte)i;
            await writer.WriteFrameAsync(new(ProtocolMessageType.Error, payload), CancellationToken.None);
        }
        await writer.FlushAsync(CancellationToken.None);

        // Assert
        stream.WriteLengths.Count.ShouldBe(expectedWrites);
        stream.Position = 0;
        await using var reader = ProtocolFrameReader.Create(stream, true);
        for (int i = 0; i < count; i++)
        {
            (await reader.ReadBorrowedFrameAsync(CancellationToken.None))!.Value.Payload.Span[0].ShouldBe((byte)i);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Owned payloads survive pooled reads and pooled bytes are cleared on return")]
    public async Task ReadBorrowedFrameAsync_OwnedAndBorrowedFrames_ShouldHonorTheirLifetimes()
    {
        // Arrange
        using var stream = new MemoryStream();
        await using (var writer = ProtocolFrameWriter.Create(stream, true))
        {
            for (int i = 0; i < 3; i++) { await writer.WriteFrameAsync(new(ProtocolMessageType.Error, new byte[] { 7, 8, 9 }), CancellationToken.None); }
        }
        stream.Position = 0;
        long rented = ProtocolEventSource.Log.RentedBuffers;
        long returned = ProtocolEventSource.Log.ReturnedBuffers;
        var reader = ProtocolFrameReader.Create(stream, true);

        // Act
        var owned = (await reader.ReadFrameAsync(CancellationToken.None))!.Value;
        var borrowed = (await reader.ReadBorrowedFrameAsync(CancellationToken.None))!.Value;
        (await reader.ReadFrameAsync(CancellationToken.None)).ShouldNotBeNull();
        await reader.DisposeAsync();
        await reader.DisposeAsync();

        // Assert
        owned.Payload.ToArray().ShouldBe([7, 8, 9]);
        borrowed.Payload.ToArray().ShouldBe([0, 0, 0]);
        (ProtocolEventSource.Log.RentedBuffers - rented).ShouldBe(1);
        (ProtocolEventSource.Log.ReturnedBuffers - returned).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - Partial borrowed frames release their rental")]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ReadBorrowedFrameAsync_PartialFrame_ShouldReturnEveryRental(int length)
    {
        // Arrange
        byte[] bytes = [0, 0, 0, 3, (byte)ProtocolMessageType.Error, 1, 2, 3];
        using var stream = new MemoryStream(bytes, 0, length);
        long balance = ProtocolEventSource.Log.RentedBuffers - ProtocolEventSource.Log.ReturnedBuffers;
        await using var reader = ProtocolFrameReader.Create(stream, true);

        // Act / Assert
        if (length == 0) { (await reader.ReadBorrowedFrameAsync(CancellationToken.None)).ShouldBeNull(); }
        else { await Should.ThrowAsync<ProtocolException>(async () => await reader.ReadBorrowedFrameAsync(CancellationToken.None)); }
        (ProtocolEventSource.Log.RentedBuffers - ProtocolEventSource.Log.ReturnedBuffers).ShouldBe(balance);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - A canceled or failed borrowed payload read releases its rental")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadBorrowedFrameAsync_PayloadReadFailure_ShouldReleaseItsRental(bool fail)
    {
        // Arrange
        using var stream = new InterruptedPayloadStream(fail);
        await using var reader = ProtocolFrameReader.Create(stream, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        long balance = ProtocolEventSource.Log.RentedBuffers - ProtocolEventSource.Log.ReturnedBuffers;

        // Act / Assert
        Task<ProtocolFrame?> read = reader.ReadBorrowedFrameAsync(cancel.Token).AsTask();
        await stream.PayloadEntered.Task.WaitAsync(timeout.Token);
        if (fail) { await Should.ThrowAsync<IOException>(async () => await read); }
        else
        {
            await cancel.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        }
        (ProtocolEventSource.Log.RentedBuffers - ProtocolEventSource.Log.ReturnedBuffers).ShouldBe(balance);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Disposing an unflushed result discards bytes and clears the batch rental")]
    public async Task DisposeAsync_UnflushedBatch_ShouldDiscardAndReturnStorage()
    {
        // Arrange
        using var stream = new RecordingWriteStream();
        long balance = ProtocolEventSource.Log.RentedBuffers - ProtocolEventSource.Log.ReturnedBuffers;
        var writer = ProtocolFrameWriter.CreateBatched(stream, true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        await writer.WriteFrameAsync(new(ProtocolMessageType.Error, new byte[] { 42 }), CancellationToken.None);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await writer.WriteFrameAsync(new(ProtocolMessageType.Error, new byte[] { 43 }), cancelled.Token));
        await writer.DisposeAsync();
        await writer.DisposeAsync();

        // Assert
        stream.WriteLengths.ShouldBeEmpty();
        (ProtocolEventSource.Log.RentedBuffers - ProtocolEventSource.Log.ReturnedBuffers).ShouldBe(balance);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await writer.FlushAsync(CancellationToken.None));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - Failed writes release pooled storage for batched and oversized frames")]
    [InlineData(100)]
    [InlineData(100_000)]
    public async Task WriteFrameAsync_TransportFailure_ShouldReleasePooledStorage(int payloadSize)
    {
        // Arrange
        using var stream = new BackpressuredWriteStream(fail: true);
        long balance = ProtocolEventSource.Log.PooledBuffersInUse;
        var writer = ProtocolFrameWriter.CreateBatched(stream, true);

        // Act
        await Should.ThrowAsync<IOException>(async () =>
        {
            await writer.WriteFrameAsync(new(ProtocolMessageType.Error, new byte[payloadSize]), CancellationToken.None);
            await writer.FlushAsync(CancellationToken.None);
        });
        await writer.DisposeAsync();

        // Assert
        ProtocolEventSource.Log.PooledBuffersInUse.ShouldBe(balance);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Backpressured batch cancellation joins I/O before pool return")]
    public async Task FlushAsync_CancellationDuringBackpressure_ShouldReleaseAllBuffers()
    {
        // Arrange: exercise cancellation while an actual batch write owns the pooled memory.
        var clock = Stopwatch.StartNew();
        int blockedWrites = 0;
        long balance = ProtocolEventSource.Log.RentedBuffers - ProtocolEventSource.Log.ReturnedBuffers;
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act: a minimum duration and observed blocked writes, with a ceiling, never a fixed race window.
        do
        {
            using var stream = new BackpressuredWriteStream();
            await using var writer = ProtocolFrameWriter.CreateBatched(stream, true);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
            await writer.WriteFrameAsync(new(ProtocolMessageType.Error, new byte[] { 42 }), cancel.Token);
            Task flush = writer.FlushAsync(cancel.Token).AsTask();
            await stream.Entered.Task.WaitAsync(ceiling.Token);
            blockedWrites++;
            await cancel.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(async () => await flush);
            await writer.DisposeAsync();
            ceiling.Token.ThrowIfCancellationRequested();
        }
        while (clock.Elapsed < TimeSpan.FromMilliseconds(100) || blockedWrites < 10);

        // Assert
        blockedWrites.ShouldBeGreaterThanOrEqualTo(10);
        (ProtocolEventSource.Log.RentedBuffers - ProtocolEventSource.Log.ReturnedBuffers).ShouldBe(balance);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Transport and pool counters publish exact late-attach values")]
    public async Task Counters_LateAttach_ShouldPublishTransportAndOwnershipCounters()
    {
        // Arrange
        using var recorder = new Assimalign.Cohesion.Testing.EventSourceRecorder(ProtocolEventSource.Log,
            EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await recorder.WaitForCountersAsync(["transport-write-calls", "transport-flush-calls", "pooled-buffers-in-use"], timeout.Token);

        // Assert
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }
}
