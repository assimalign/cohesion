using Assimalign.Cohesion.Web.Diagnostics.Internal;

using Shouldly;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

/// <summary>
/// Covers the span-based <c>traceparent</c> parser used for the OpenTelemetry correlation
/// boundary (trace/span ids as attributes; no OTel dependency).
/// </summary>
public class TraceParentTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - TraceParent: Should parse a valid header")]
    public void TryParse_ValidHeader_ShouldExtractIds()
    {
        // Act
        bool parsed = TraceParent.TryParse("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", out string traceId, out string spanId);

        // Assert
        parsed.ShouldBeTrue();
        traceId.ShouldBe("0af7651916cd43dd8448eb211c80319c");
        spanId.ShouldBe("b7ad6b7169203331");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - TraceParent: Should accept future versions with trailing fields")]
    public void TryParse_FutureVersionWithSuffix_ShouldExtractIds()
    {
        // Act — later trace-context versions may append '-' separated fields.
        bool parsed = TraceParent.TryParse("01-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01-extra", out string traceId, out _);

        // Assert
        parsed.ShouldBeTrue();
        traceId.ShouldBe("0af7651916cd43dd8448eb211c80319c");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - TraceParent: Should reject malformed headers")]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331")]                      // too short
    [InlineData("ff-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")]                   // forbidden version
    [InlineData("00-00000000000000000000000000000000-b7ad6b7169203331-01")]                   // all-zero trace id
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-0000000000000000-01")]                   // all-zero span id
    [InlineData("00-0AF7651916CD43DD8448EB211C80319C-b7ad6b7169203331-01")]                   // uppercase hex
    [InlineData("00_0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")]                   // wrong separator
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01x")]                  // suffix without separator
    public void TryParse_MalformedHeader_ShouldReturnFalse(string value)
    {
        TraceParent.TryParse(value, out _, out _).ShouldBeFalse();
    }
}
