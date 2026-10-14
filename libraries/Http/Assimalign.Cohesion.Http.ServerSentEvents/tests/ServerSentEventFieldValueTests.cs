using System;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ServerSentEvents.Tests;

public class ServerSentEventFieldValueTests
{
    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - IsValid: Invalid candidates fail without echoing the value")]
    [InlineData(null)]
    [InlineData("secret\rvalue")]
    [InlineData("secret\nvalue")]
    [InlineData("secret\r\nvalue")]
    public void IsValid_InvalidCandidate_ShouldReturnFalseAndSafeReason(string? candidate)
    {
        // Arrange
        string? message;

        // Act
        bool valid = ServerSentEventFieldValue.IsValid(candidate!, out message);
        bool created = ServerSentEventFieldValue.TryCreate(candidate, out ServerSentEventFieldValue result);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrEmpty();
        message.ShouldNotContain("secret", Case.Sensitive);
        created.ShouldBeFalse();
        result.ShouldBe(default);
        result.Value.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Constructor: Invalid values throw without echoing the value")]
    [InlineData(null)]
    [InlineData("secret\rvalue")]
    [InlineData("secret\nvalue")]
    [InlineData("secret\r\nvalue")]
    public void Constructor_InvalidCandidate_ShouldThrowWithoutEchoingInput(string? candidate)
    {
        // Arrange
        string value = candidate!;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new ServerSentEventFieldValue(value));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldNotContain("secret", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ServerSentEvents] - TryCreate: Empty and single-line Unicode values are preserved")]
    [InlineData("")]
    [InlineData("update")]
    [InlineData("\0")]
    [InlineData("phase\0ready")]
    [InlineData("42")]
    [InlineData(": data: literal field value")]
    [InlineData("tab\tvalue")]
    [InlineData("café 更新 ✓")]
    [InlineData("other separators: \u0085\u2028\u2029")]
    public void TryCreate_ValidCandidate_ShouldPreserveValueAcrossCreationPaths(string candidate)
    {
        // Arrange
        string? optionalCandidate = candidate;

        // Act
        bool valid = ServerSentEventFieldValue.IsValid(candidate, out string? message);
        bool created = ServerSentEventFieldValue.TryCreate(candidate, out ServerSentEventFieldValue result);
        ServerSentEventFieldValue converted = candidate;
        ServerSentEventFieldValue? optional = optionalCandidate;

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        created.ShouldBeTrue();
        result.Value.ShouldBe(candidate);
        converted.ShouldBe(result);
        optional.HasValue.ShouldBeTrue();
        optional.GetValueOrDefault().ShouldBe(result);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Nullable conversion: A null string remains an absent value")]
    public void NullableConversion_NullString_ShouldRemainAbsent()
    {
        // Arrange
        string? value = null;

        // Act
        ServerSentEventFieldValue? field = value;
        ServerSentEvent @event = new("payload") { EventType = value, Comment = value };

        // Assert
        field.HasValue.ShouldBeFalse();
        @event.EventType.HasValue.ShouldBeFalse();
        @event.Comment.HasValue.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ServerSentEvents] - Format: Default field values omit optional metadata")]
    public void Format_DefaultFieldValues_ShouldOmitMetadata()
    {
        // Arrange
        ServerSentEventFieldValue value = default;
        ServerSentEvent @event = new("payload") { EventType = value, Comment = value };

        // Act
        byte[] actual = ServerSentEventFormatter.Format(@event);

        // Assert
        value.Value.ShouldBeNull();
        actual.ShouldBe(Encoding.UTF8.GetBytes("data: payload\n\n"));
    }
}
