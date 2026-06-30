using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections.Tests;

public class DuplexPipePairTests
{
    private static PipeOptions CreateOptions(long pauseWriterThreshold, long resumeWriterThreshold)
        => new(
            pauseWriterThreshold: pauseWriterThreshold,
            resumeWriterThreshold: resumeWriterThreshold,
            useSynchronizationContext: false);

    [Fact]
    public async Task Create_WhenPumpWritesToTransportOutput_ShouldDeliverBytesToConsumerInput()
    {
        // Arrange
        DuplexPipePair pair = DuplexPipePair.Create(
            new PipeOptions(useSynchronizationContext: false),
            new PipeOptions(useSynchronizationContext: false));
        byte[] payload = [1, 2, 3, 4];

        // Act
        await pair.TransportOutput.WriteAsync(payload);

        // Assert
        ReadResult result = await pair.Input.ReadAsync();
        result.Buffer.ToArray().ShouldBe(payload);
        pair.Input.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task Create_WhenConsumerWritesToOutput_ShouldDeliverBytesToTransportInput()
    {
        // Arrange
        DuplexPipePair pair = DuplexPipePair.Create(
            new PipeOptions(useSynchronizationContext: false),
            new PipeOptions(useSynchronizationContext: false));
        byte[] payload = [5, 6, 7, 8];

        // Act
        await pair.Output.WriteAsync(payload);

        // Assert
        ReadResult result = await pair.TransportInput.ReadAsync();
        result.Buffer.ToArray().ShouldBe(payload);
        pair.TransportInput.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task Create_WithSmallInputPauseThreshold_ShouldApplyInputOptionsToReceivePipe()
    {
        // Arrange: the receive (wire-to-consumer) pipe pauses after 4 bytes; the send pipe is huge.
        DuplexPipePair pair = DuplexPipePair.Create(
            CreateOptions(pauseWriterThreshold: 4, resumeWriterThreshold: 2),
            CreateOptions(pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19));

        // Act: writing past the receive pipe's pause threshold leaves the flush incomplete.
        ValueTask<FlushResult> flush = pair.TransportOutput.WriteAsync(new byte[16]);

        // Assert
        flush.IsCompleted.ShouldBeFalse();

        // Draining the consumer side resumes the writer and completes the flush.
        ReadResult result = await pair.Input.ReadAsync();
        pair.Input.AdvanceTo(result.Buffer.End);
        FlushResult flushResult = await flush;
        flushResult.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Create_WithSmallOutputPauseThreshold_ShouldApplyOutputOptionsToSendPipe()
    {
        // Arrange: the send (consumer-to-wire) pipe pauses after 4 bytes; the receive pipe is huge.
        DuplexPipePair pair = DuplexPipePair.Create(
            CreateOptions(pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19),
            CreateOptions(pauseWriterThreshold: 4, resumeWriterThreshold: 2));

        // Act: the receive pipe absorbs the same payload without pausing...
        ValueTask<FlushResult> receiveFlush = pair.TransportOutput.WriteAsync(new byte[16]);

        // ...while the send pipe pauses on it.
        ValueTask<FlushResult> sendFlush = pair.Output.WriteAsync(new byte[16]);

        // Assert
        receiveFlush.IsCompleted.ShouldBeTrue();
        sendFlush.IsCompleted.ShouldBeFalse();

        // Draining the pump side resumes the consumer's writer.
        ReadResult result = await pair.TransportInput.ReadAsync();
        pair.TransportInput.AdvanceTo(result.Buffer.End);
        FlushResult sendFlushResult = await sendFlush;
        sendFlushResult.IsCompleted.ShouldBeFalse();
        await receiveFlush;
    }
}
