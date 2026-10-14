using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.ServerSentEvents.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.ServerSentEvents.Tests;

// These bytes match afa78be23; d6417c725 incorrectly rejects these valid NUL values.
public class ServerSentEventNulCompatibilityTests
{
    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Format: NUL event types preserve the base wire bytes")]
    [InlineData("\0")]
    [InlineData("phase\0ready")]
    public void Format_NulEventType_ShouldPreserveExistingWireBytes(string value)
    {
        // Arrange
        byte[] expected = Encoding.UTF8.GetBytes("event: " + value + "\ndata: payload\n\n");

        // Act
        ServerSentEvent @event = new("payload") { EventType = value };
        byte[] actual = ServerSentEventFormatter.Format(@event);

        // Assert
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Format: NUL comments preserve the base wire bytes")]
    [InlineData("\0")]
    [InlineData("phase\0ready")]
    public void Format_NulComment_ShouldPreserveExistingWireBytes(string value)
    {
        // Arrange
        byte[] expected = Encoding.UTF8.GetBytes(": " + value + "\ndata: payload\n\n");

        // Act
        ServerSentEvent @event = new("payload") { Comment = value };
        byte[] actual = ServerSentEventFormatter.Format(@event);

        // Assert
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - KeepAlive: NUL comments preserve the base heartbeat bytes")]
    [InlineData("\0")]
    [InlineData("phase\0ready")]
    public void KeepAlive_NulComment_ShouldPreserveExistingWireBytes(string comment)
    {
        // Arrange
        byte[] expected = Encoding.UTF8.GetBytes(": " + comment + "\n\n");

        // Act
        byte[] actual = ServerSentEventFormatter.Format(ServerSentEvent.KeepAlive(comment));

        // Assert
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - WriteKeepAliveAsync: NUL comments preserve and flush the base heartbeat bytes")]
    [InlineData("\0")]
    [InlineData("phase\0ready")]
    public async Task WriteKeepAliveAsync_NulComment_ShouldPreserveExistingWireBytes(string comment)
    {
        // Arrange
        RecordingServerSentEventStreamingFeature streaming = new();
        byte[] expected = Encoding.UTF8.GetBytes(": " + comment + "\n\n");

        // Act
        await streaming.WriteKeepAliveAsync(comment, CancellationToken.None);

        // Assert
        streaming.HasStarted.ShouldBeTrue();
        streaming.WrittenBytes.ShouldBe(expected);
        streaming.FlushCount.ShouldBe(1);
    }
}
