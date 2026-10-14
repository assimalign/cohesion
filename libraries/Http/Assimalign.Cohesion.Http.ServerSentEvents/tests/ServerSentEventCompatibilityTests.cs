using System;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ServerSentEvents.Tests;

// These compatibility tests also pass at afa78be23; they pin the existing valid wire format.
public class ServerSentEventCompatibilityTests
{
    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Format: Valid events preserve every UTF-8 byte")]
    public void Format_ValidEvent_ShouldPreserveExistingWireBytes()
    {
        // Arrange
        ServerSentEvent @event = new("approved payload")
        {
            Comment = "note: café",
            EventType = "更新",
            Id = "42:✓",
            Retry = TimeSpan.FromMilliseconds(3000),
        };
        byte[] expected = Encoding.UTF8.GetBytes(": note: café\nevent: 更新\nid: 42:✓\nretry: 3000\ndata: approved payload\n\n");

        // Act
        byte[] actual = ServerSentEventFormatter.Format(@event);

        // Assert
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Format: Data keeps CR, LF and CRLF as separate data lines")]
    [InlineData("first\rsecond", "data: first\ndata: second\n\n")]
    [InlineData("first\nsecond", "data: first\ndata: second\n\n")]
    [InlineData("first\r\nsecond", "data: first\ndata: second\n\n")]
    [InlineData("first\r\nsecond\rthird\nfourth", "data: first\ndata: second\ndata: third\ndata: fourth\n\n")]
    [InlineData("first\n\nevent: payload text\n", "data: first\ndata: \ndata: event: payload text\ndata: \n\n")]
    public void Format_MultilineData_ShouldPreserveExistingDataLineSplitting(string data, string expected)
    {
        // Arrange
        ServerSentEvent @event = ServerSentEvent.Message(data);

        // Act
        byte[] actual = ServerSentEventFormatter.Format(@event);

        // Assert
        actual.ShouldBe(Encoding.UTF8.GetBytes(expected));
    }

    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Format: Null metadata still omits every optional field")]
    public void Format_NullMetadata_ShouldPreserveExistingOmittedFields()
    {
        // Arrange
        string? missing = null;
        ServerSentEvent literalNull = new("payload") { EventType = null, Id = null, Comment = null };
        ServerSentEvent nullableString = new("payload") { EventType = missing, Id = missing, Comment = missing };
        byte[] expected = Encoding.UTF8.GetBytes("data: payload\n\n");

        // Act
        byte[] literalBytes = ServerSentEventFormatter.Format(literalNull);
        byte[] nullableBytes = ServerSentEventFormatter.Format(nullableString);

        // Assert
        literalBytes.ShouldBe(expected);
        nullableBytes.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Format: Empty id resets the stored id and empty comments remain present")]
    public void Format_EmptyMetadata_ShouldPreserveExistingCommentAndIdReset()
    {
        // Arrange
        ServerSentEvent @event = new("payload") { EventType = string.Empty, Id = string.Empty, Comment = string.Empty };

        // Act
        byte[] actual = ServerSentEventFormatter.Format(@event);

        // Assert
        actual.ShouldBe(Encoding.UTF8.GetBytes(": \nid: \ndata: payload\n\n"));
    }
}
