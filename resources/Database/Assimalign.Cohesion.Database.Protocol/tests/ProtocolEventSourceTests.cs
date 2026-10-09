using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Protocol.Internal;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

/// <summary>
/// Serializes the tests that observe the protocol event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(ProtocolEventSourceCollection), DisableParallelization = true)]
public class ProtocolEventSourceCollection
{
}

/// <summary>
/// The protocol's event source against the repository's EventSource convention: the frame trace
/// reports each frame once where it crosses the transport, only under its keyword, and costs the
/// frame reader and writer nothing while nobody listens.
/// </summary>
[Collection(nameof(ProtocolEventSourceCollection))]
public sealed class ProtocolEventSourceTests
{
    private static readonly ProtocolMessageFamily Family = new("model", 64);

    private readonly ITestOutputHelper _output;

    public ProtocolEventSourceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - ProtocolEventSource: Should be named for its assembly")]
    public void GetName_ProtocolEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(ProtocolEventSource));

        // Assert
        name.ShouldBe(typeof(ProtocolEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Protocol");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - ProtocolEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(ProtocolEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Protocol", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - ProtocolEventSource: Should report each frame once through a channel, with its type and payload length")]
    public async Task FrameTrace_FramesThroughChannel_ShouldReportEachFrameOnce()
    {
        // Arrange: the channel's reader and writer decorate the stream ones, so a frame passes two
        // public members on its way to the transport and must still be reported once.
        using var stream = new MemoryStream();
        using var recorder = new EventSourceRecorder(ProtocolEventSource.Log, EventLevel.Verbose, ProtocolEventSource.Keywords.Frames);

        // Act
        await using (var channel = new ProtocolChannel(stream, Family, leaveOpen: true))
        {
            await channel.Writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty));
            await channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)64, new byte[] { 1, 2, 3 }));
            await channel.Writer.FlushAsync();
        }

        stream.Position = 0;
        await using (var channel = new ProtocolChannel(stream, Family, leaveOpen: true))
        {
            (await channel.Reader.ReadFrameAsync())!.Value.Type.ShouldBe(ProtocolMessageType.Ping);
            (await channel.Reader.ReadFrameAsync())!.Value.Type.ShouldBe((ProtocolMessageType)64);
            (await channel.Reader.ReadFrameAsync()).ShouldBeNull();
        }

        // Assert
        var events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        events.Select(e => (e.EventName, Type: (string)e.Payload![0]!, Length: (int)e.Payload[1]!)).ShouldBe(
        [
            ("FrameWritten", "Ping", 0),
            ("FrameWritten", "64", 3),
            ("FrameRead", "Ping", 0),
            ("FrameRead", "64", 3),
        ]);

        var written = events[0];
        written.EventId.ShouldBe(2);
        written.Level.ShouldBe(EventLevel.Verbose);
        (written.Keywords & ProtocolEventSource.Keywords.Frames).ShouldBe(ProtocolEventSource.Keywords.Frames);
        written.PayloadNames.ShouldBe(["messageType", "payloadLength"]);

        var read = events[2];
        read.EventId.ShouldBe(1);
        read.Level.ShouldBe(EventLevel.Verbose);
        (read.Keywords & ProtocolEventSource.Keywords.Frames).ShouldBe(ProtocolEventSource.Keywords.Frames);
        read.PayloadNames.ShouldBe(["messageType", "payloadLength"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - ProtocolEventSource: Should write no frame without the Frames keyword")]
    public async Task FrameTrace_WithoutFramesKeyword_ShouldWriteNothing()
    {
        // Arrange: Verbose, but under a keyword bit the source does not use for frames.
        using var stream = new MemoryStream();
        using var recorder = new EventSourceRecorder(ProtocolEventSource.Log, EventLevel.Verbose, (EventKeywords)0x2);

        // Act
        await using (var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true))
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty));
        }

        stream.Position = 0;
        await using (var reader = ProtocolFrameReader.Create(stream, leaveOpen: true))
        {
            (await reader.ReadFrameAsync()).ShouldNotBeNull();
        }

        // Assert
        recorder.Events.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - ProtocolEventSource: Should report a frame whose transport completes asynchronously once")]
    public async Task FrameTrace_AsynchronousTransport_ShouldReportEachFrameOnce()
    {
        // Arrange: a stream that yields before every read and write, so the reader and writer take
        // the traced asynchronous path instead of the synchronous one.
        using var stream = new YieldingStream();
        using var recorder = new EventSourceRecorder(ProtocolEventSource.Log, EventLevel.Verbose, ProtocolEventSource.Keywords.Frames);

        // Act
        await using (var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true))
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Terminate, new byte[] { 7, 7 }));
        }

        stream.Position = 0;
        await using (var reader = ProtocolFrameReader.Create(stream, leaveOpen: true))
        {
            (await reader.ReadFrameAsync())!.Value.Payload.Length.ShouldBe(2);
        }

        // Assert
        recorder.Events.Select(e => (e.EventName, Type: (string)e.Payload![0]!, Length: (int)e.Payload[1]!)).ShouldBe(
        [
            ("FrameWritten", "Terminate", 2),
            ("FrameRead", "Terminate", 2),
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - ProtocolEventSource: The frame reader allocates no more than its stream core while nobody listens")]
    public async Task ReadFrameAsync_NoListener_ShouldAllocateNoMoreThanItsCore()
    {
        // Arrange: empty-payload frames over a memory stream complete synchronously and allocate no
        // payload. The baseline is the stream reader's own core, called through a delegate, so the
        // check holds in Debug, where the core's async state machines are objects, and in Release,
        // where both measure zero.
        const int frames = 1_000;
        ProtocolEventSource.Log.IsEnabled().ShouldBeFalse("A listener from another test is still attached.");
        using var stream = new MemoryStream(EmptyFrames(2 * (frames + 100)), writable: false);
        ProtocolFrameReader reader = ProtocolFrameReader.Create(stream, leaveOpen: true);
        var core = typeof(ProtocolStreamFrameReader)
            .GetMethod("ReadFrameCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<CancellationToken, ValueTask<ProtocolFrame?>>>(reader);
        for (int index = 0; index < 100; index++)
        {
            (await reader.ReadFrameAsync()).ShouldNotBeNull();
            (await core(CancellationToken.None)).ShouldNotBeNull();
        }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < frames; index++)
        {
            _ = await core(CancellationToken.None);
        }

        long coreAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < frames; index++)
        {
            _ = await reader.ReadFrameAsync();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"ReadFrameAsync, {frames} frames, no listener: {allocated} bytes; ReadFrameCoreAsync alone: {coreAllocated} bytes.");

        // Assert
        allocated.ShouldBe(coreAllocated);
#if !DEBUG
        allocated.ShouldBe(0L);
#endif
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - ProtocolEventSource: The frame writer allocates no more than its stream core while nobody listens")]
    public async Task WriteFrameAsync_NoListener_ShouldAllocateNoMoreThanItsCore()
    {
        // Arrange: a memory stream with room for every frame, so the stream never grows; the
        // baseline is the stream writer's own core, as for the reader.
        const int frames = 1_000;
        ProtocolEventSource.Log.IsEnabled().ShouldBeFalse("A listener from another test is still attached.");
        using var stream = new MemoryStream(ProtocolFrameHeader.Size * 2 * (frames + 100));
        ProtocolFrameWriter writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);
        var core = typeof(ProtocolStreamFrameWriter)
            .GetMethod("WriteFrameCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<ProtocolFrame, CancellationToken, ValueTask>>(writer);
        var frame = new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty);
        for (int index = 0; index < 100; index++)
        {
            await writer.WriteFrameAsync(frame);
            await core(frame, CancellationToken.None);
        }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < frames; index++)
        {
            await core(frame, CancellationToken.None);
        }

        long coreAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < frames; index++)
        {
            await writer.WriteFrameAsync(frame);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"WriteFrameAsync, {frames} frames, no listener: {allocated} bytes; WriteFrameCoreAsync alone: {coreAllocated} bytes.");

        // Assert
        allocated.ShouldBe(coreAllocated);
#if !DEBUG
        allocated.ShouldBe(0L);
#endif
    }

    private static byte[] EmptyFrames(int count)
    {
        byte[] wire = new byte[ProtocolFrameHeader.Size * count];
        for (int index = 0; index < count; index++)
        {
            new ProtocolFrameHeader(ProtocolMessageType.Ping, 0).WriteTo(wire.AsSpan(index * ProtocolFrameHeader.Size));
        }

        return wire;
    }

    /// <summary>
    /// A memory stream whose asynchronous reads and writes yield first, so they never complete
    /// synchronously.
    /// </summary>
    private sealed class YieldingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return Read(buffer.Span);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Write(buffer.Span);
        }
    }
}
