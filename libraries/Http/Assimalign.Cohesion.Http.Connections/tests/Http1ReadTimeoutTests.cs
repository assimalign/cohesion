using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Buffered reads and pending reads share the same two-phase deadline.</summary>
public class Http1ReadTimeoutTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Read Timeout: Buffered input should still observe an elapsed phase deadline")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_OnBufferedInputAfterDeadline_ShouldCancelInCurrentPhase(bool headers)
    {
        // Arrange — no pending read has needed a source yet. Reading a buffered prefix must not
        // give the rest of the head a fresh deadline when the next read eventually waits.
        Pipe pipe = new();
        await pipe.Writer.WriteAsync("GE"u8.ToArray());
        using Http1ReadTimeout timeout = new(CancellationToken.None, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        if (headers)
        {
            ReadResult prefix = await timeout.ReadAsync(pipe.Reader);
            pipe.Reader.AdvanceTo(prefix.Buffer.GetPosition(1));
            timeout.OnRequestLineStarted();
        }
        await Task.Delay(100);

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await timeout.ReadAsync(pipe.Reader));

        // Assert
        timeout.TimedOut.ShouldBeTrue();
        timeout.IsHeadersPhase.ShouldBe(headers);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Read Timeout: A graceful close should cancel a pending idle read")]
    public async Task ReadAsync_OnGracefulCloseDuringIdleWait_ShouldCancelWithoutHeadersPhase()
    {
        // Arrange
        Pipe pipe = new();
        using Http1ReadTimeout timeout = new(CancellationToken.None, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        Task<ReadResult> read = timeout.ReadAsync(pipe.Reader).AsTask();

        // Act
        timeout.CancelIdleWait();
        await Should.ThrowAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert
        timeout.TimedOut.ShouldBeTrue();
        timeout.IsHeadersPhase.ShouldBeFalse();
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Read Timeout: Connection cancellation should cancel buffered reads without reporting a timeout")]
    public async Task ReadAsync_OnConnectionCancellationWithBufferedInput_ShouldKeepCancellationOrigin()
    {
        // Arrange
        Pipe pipe = new();
        await pipe.Writer.WriteAsync("GET"u8.ToArray());
        using CancellationTokenSource connection = new();
        using Http1ReadTimeout timeout = new(connection.Token, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        connection.Cancel();

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(async () => await timeout.ReadAsync(pipe.Reader));

        // Assert
        failure.CancellationToken.ShouldBe(connection.Token);
        timeout.TimedOut.ShouldBeFalse();
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }
}
