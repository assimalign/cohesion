using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.ServerSentEvents.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.ServerSentEvents.Tests;

public class ServerSentEventFieldInjectionTests
{
    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Assign: Each metadata field rejects CR, LF, CRLF and NUL")]
    [InlineData("event", "\r")]
    [InlineData("event", "\n")]
    [InlineData("event", "\r\n")]
    [InlineData("event", "\0")]
    [InlineData("id", "\r")]
    [InlineData("id", "\n")]
    [InlineData("id", "\r\n")]
    [InlineData("id", "\0")]
    [InlineData("comment", "\r")]
    [InlineData("comment", "\n")]
    [InlineData("comment", "\r\n")]
    [InlineData("comment", "\0")]
    public void Assign_MetadataWithForbiddenCharacter_ShouldThrowWithoutEchoingInput(string field, string forbidden)
    {
        // Arrange
        string value = "untrusted-value" + forbidden + "suffix";

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => CreateEvent(field, value));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldNotContain("untrusted-value", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Write: Untrusted metadata cannot inject fields or events")]
    [InlineData("event", "\r", false)]
    [InlineData("event", "\n", false)]
    [InlineData("event", "\r\n", false)]
    [InlineData("id", "\r", false)]
    [InlineData("id", "\n", false)]
    [InlineData("id", "\r\n", false)]
    [InlineData("comment", "\r", false)]
    [InlineData("comment", "\n", false)]
    [InlineData("comment", "\r\n", false)]
    [InlineData("event", "\r", true)]
    [InlineData("event", "\n", true)]
    [InlineData("event", "\r\n", true)]
    [InlineData("id", "\r", true)]
    [InlineData("id", "\n", true)]
    [InlineData("id", "\r\n", true)]
    [InlineData("comment", "\r", true)]
    [InlineData("comment", "\n", true)]
    [InlineData("comment", "\r\n", true)]
    public void Write_InjectedMetadata_ShouldLeaveWriterEmptyThenWriteOnlyRequestedEvent(string field, string delimiter, bool injectEvent)
    {
        // Arrange
        string value = injectEvent
            ? "original" + delimiter + delimiter + "event: forged" + delimiter + "data: injected"
            : "original" + delimiter + "data: injected";
        ArrayBufferWriter<byte> writer = new();
        ServerSentEvent requested = new("approved payload") { EventType = "update", Id = "42", Comment = "note" };
        byte[] expected = Encoding.UTF8.GetBytes(": note\nevent: update\nid: 42\ndata: approved payload\n\n");

        // Act
        Should.Throw<ArgumentException>(() => ServerSentEventFormatter.Write(CreateEvent(field, value), writer));

        // Assert
        writer.WrittenCount.ShouldBe(0);
        ServerSentEventFormatter.Write(requested, writer);
        writer.WrittenSpan.ToArray().ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - KeepAlive: Invalid comments are rejected at creation")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\0")]
    public void KeepAlive_InvalidComment_ShouldThrowBeforeCreatingHeartbeat(string forbidden)
    {
        // Arrange
        string comment = "heartbeat" + forbidden + "data: injected";

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => ServerSentEvent.KeepAlive(comment));

        // Assert
        exception.Message.ShouldNotContain("heartbeat", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - WriteKeepAliveAsync: Rejected comments never start or write the response")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\0")]
    public async Task WriteKeepAliveAsync_InvalidComment_ShouldLeaveResponseUnstartedThenWriteValidHeartbeat(string forbidden)
    {
        // Arrange
        RecordingServerSentEventStreamingFeature streaming = new();
        string comment = "heartbeat" + forbidden + "data: injected";

        // Act
        await Should.ThrowAsync<ArgumentException>(async () =>
            await streaming.WriteKeepAliveAsync(comment, CancellationToken.None));

        // Assert
        streaming.HasStarted.ShouldBeFalse();
        streaming.WrittenBytes.ShouldBeEmpty();
        streaming.FlushCount.ShouldBe(0);
        await streaming.WriteKeepAliveAsync("approved heartbeat", CancellationToken.None);
        streaming.WrittenBytes.ShouldBe(Encoding.UTF8.GetBytes(": approved heartbeat\n\n"));
        streaming.FlushCount.ShouldBe(1);
    }

    private static ServerSentEvent CreateEvent(string field, string value)
    {
        return field switch
        {
            "event" => new ServerSentEvent("approved payload") { EventType = value },
            "id" => new ServerSentEvent("approved payload") { Id = value },
            "comment" => new ServerSentEvent("approved payload") { Comment = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }
}
