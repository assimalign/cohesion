using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.ServerSentEvents.Tests.TestObjects;

internal sealed class RecordingServerSentEventStreamingFeature : IHttpResponseStreamingFeature
{
    private readonly ArrayBufferWriter<byte> _writer = new();

    public string Name => "Assimalign.Cohesion.Http.ResponseStreaming";
    public bool HasStarted { get; private set; }
    public int FlushCount { get; private set; }
    public byte[] WrittenBytes => _writer.WrittenSpan.ToArray();

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        HasStarted = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        HasStarted = true;
        _writer.Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        HasStarted = true;
        FlushCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
