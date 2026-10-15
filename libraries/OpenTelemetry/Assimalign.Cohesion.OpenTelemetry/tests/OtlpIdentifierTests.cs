using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenTelemetry.Tests;

public sealed class OtlpIdentifierTests
{
    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - Identifiers: accept mixed case and compare ordinally ignoring case")]
    public void Equals_WithDifferentHexCase_ShouldPreserveIdentityAndSpelling()
    {
        // Arrange
        const string traceText = "aBcDeF0123456789aBcDeF0123456789";
        const string spanText = "aBcDeF0123456789";
        var trace = new OtlpTraceId(traceText);
        var span = new OtlpSpanId(spanText);

        // Act
        OtlpTraceId.TryCreate(traceText.ToUpperInvariant(), out var otherTrace).ShouldBeTrue();
        OtlpSpanId.TryCreate(spanText.ToUpperInvariant(), out var otherSpan).ShouldBeTrue();

        // Assert
        trace.ShouldBe(otherTrace);
        span.ShouldBe(otherSpan);
        trace.GetHashCode().ShouldBe(otherTrace.GetHashCode());
        span.GetHashCode().ShouldBe(otherSpan.GetHashCode());
        trace.CompareTo(otherTrace).ShouldBe(0);
        span.CompareTo(otherSpan).ShouldBe(0);
        trace.Value.ShouldBe(traceText);
        span.Value.ShouldBe(spanText);
    }

    [Theory(DisplayName = "Cohesion Test [OpenTelemetry] - Identifiers: refuse invalid text without throwing")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("abcdef0123456789abcdef012345678", "abcdef012345678")]
    [InlineData("abcdef0123456789abcdef01234567890", "abcdef01234567890")]
    [InlineData("abcdef0123456789abcdef012345678g", "abcdef012345678g")]
    [InlineData("abcdef0123456789abcdef012345678\r", "abcdef012345678\n")]
    [InlineData("abcdef0123456789abcdef012345678\uFF10", "abcdef012345678\uFF10")]
    public void TryCreate_WithInvalidText_ShouldReturnFalse(string? traceText, string? spanText)
    {
        // Arrange / Act
        bool traceCreated = OtlpTraceId.TryCreate(traceText, out var trace);
        bool spanCreated = OtlpSpanId.TryCreate(spanText, out var span);

        // Assert
        traceCreated.ShouldBeFalse();
        spanCreated.ShouldBeFalse();
        trace.ShouldBe(default);
        span.ShouldBe(default);
    }

    [Fact(DisplayName = "Cohesion Test [OpenTelemetry] - Record: with assignments validate identifiers")]
    public void With_WithMalformedIdentifier_ShouldThrow()
    {
        // Arrange
        var record = new OtlpLogRecord(DateTimeOffset.UtcNow, 9, "INFO", "body", "test", null, null, null);

        // Act / Assert
        Should.Throw<ArgumentException>(() => record with { TraceId = "bad" });
        Should.Throw<ArgumentException>(() => record with { SpanId = "bad" });
    }
}
