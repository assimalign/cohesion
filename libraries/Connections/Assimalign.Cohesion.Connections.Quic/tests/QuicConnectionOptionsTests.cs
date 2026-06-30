using System.Runtime.Versioning;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Quic.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicConnectionOptionsTests
{
    [Fact]
    public void DefaultErrorCodes_OnListenerOptions_ShouldMatchHttp3Codes()
    {
        // Arrange / Act
        // The options default ApplicationProtocols to HTTP/3, so the error codes default to
        // the matching RFC 9114 §8.1 values.
        QuicConnectionListenerOptions options = new();

        // Assert
        options.DefaultCloseErrorCode.ShouldBe(0x100);  // H3_NO_ERROR
        options.DefaultStreamErrorCode.ShouldBe(0x10c); // H3_REQUEST_CANCELLED
    }

    [Fact]
    public void DefaultErrorCodes_OnFactoryOptions_ShouldMatchHttp3Codes()
    {
        // Arrange / Act
        // The options default ApplicationProtocols to HTTP/3, so the error codes default to
        // the matching RFC 9114 §8.1 values.
        QuicConnectionFactoryOptions options = new();

        // Assert
        options.DefaultCloseErrorCode.ShouldBe(0x100);  // H3_NO_ERROR
        options.DefaultStreamErrorCode.ShouldBe(0x10c); // H3_REQUEST_CANCELLED
    }
}
