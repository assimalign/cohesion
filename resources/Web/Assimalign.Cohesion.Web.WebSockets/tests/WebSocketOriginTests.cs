using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.WebSockets.Internal;
using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.WebSockets.Tests;

public class WebSocketOriginTests
{
    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - Origin: An origin is serialized with lower case, no default port and a canonical IPv6 address")]
    [InlineData("https://app.example", "https://app.example")]
    [InlineData("HTTPS://App.Example", "https://app.example")]
    [InlineData("https://app.example:443", "https://app.example")]
    [InlineData("http://localhost:80", "http://localhost")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000", "http://[::1]:5000")]
    [InlineData("http://[0:0:0:0:0:0:0:1]", "http://[::1]")]
    [InlineData("https://[FE80:0:0:0:0:0:0:1]:443", "https://[fe80::1]")]
    public void TryNormalize_Origin_ReturnsSerializedForm(string value, string expected)
    {
        // Act
        bool valid = WebSocketOrigin.TryNormalize(value, out string? serialized);

        // Assert
        valid.ShouldBeTrue();
        serialized.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - Origin: Anything that is not an origin is rejected")]
    [InlineData("null")]
    [InlineData("app.example")]
    [InlineData("https://")]
    [InlineData("https://app.example/")]
    [InlineData("https://app.example/path")]
    [InlineData("https://app.example?q=1")]
    [InlineData("https://user@app.example")]
    [InlineData("https://*.app.example")]
    [InlineData("https://app example")]
    [InlineData("https://ü.example")]
    [InlineData("https://app.example:")]
    [InlineData("https://app.example:65536")]
    [InlineData("https://app.example:8x")]
    [InlineData("https://[::1")]
    [InlineData("https://[fe80::1%25eth0]")]
    [InlineData("1https://app.example")]
    public void TryNormalize_NotAnOrigin_ReturnsFalse(string value)
    {
        WebSocketOrigin.TryNormalize(value, out string? serialized).ShouldBeFalse();
        serialized.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - Origin: A configured origin that is not a serialized origin fails with a message naming the fix")]
    [InlineData("*", "AllowAnyOrigin")]
    [InlineData("null", "AllowAnyOrigin")]
    [InlineData("https://app.example/", "trailing")]
    [InlineData("", "empty")]
    public void NormalizeConfigured_InvalidOrigin_ThrowsArgumentException(string value, string fragment)
    {
        ArgumentException exception = Should.Throw<ArgumentException>(() => WebSocketOrigin.NormalizeConfigured(value));

        exception.Message.ShouldContain(fragment);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - Origin: The request's own origin comes from its wire scheme and host when no proxy resolved them")]
    public void TryGetRequestOrigin_WithoutForwardedFeature_UsesWireSchemeAndHost()
    {
        // Arrange
        PolicyTestContext context = new(HttpMethod.Get, HttpScheme.Http, "LocalHost:80");

        // Act
        bool valid = WebSocketOrigin.TryGetRequestOrigin(context, out string? origin);

        // Assert
        valid.ShouldBeTrue();
        origin.ShouldBe("http://localhost");
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - Origin: The request's own origin comes from the effective scheme and host a trusted proxy resolved")]
    public void TryGetRequestOrigin_WithForwardedFeature_UsesEffectiveSchemeAndHost()
    {
        // Arrange — TLS terminates at the proxy; the server sees plain HTTP on an internal host.
        PolicyTestContext context = new(HttpMethod.Get, HttpScheme.Http, "10.0.0.5:8080");
        context.Features.Set(new FakeForwardedFeature(HttpScheme.Https, "app.example", HttpScheme.Http, "10.0.0.5:8080"));

        // Act
        bool valid = WebSocketOrigin.TryGetRequestOrigin(context, out string? origin);

        // Assert
        valid.ShouldBeTrue();
        origin.ShouldBe("https://app.example");
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - Origin: A request without a known scheme or host has no origin of its own")]
    public void TryGetRequestOrigin_UnknownSchemeOrEmptyHost_ReturnsFalse()
    {
        WebSocketOrigin.TryGetRequestOrigin(new PolicyTestContext(HttpMethod.Get, HttpScheme.None, "app.example"), out _).ShouldBeFalse();
        WebSocketOrigin.TryGetRequestOrigin(new PolicyTestContext(HttpMethod.Get, HttpScheme.Https, string.Empty), out _).ShouldBeFalse();
    }
}
