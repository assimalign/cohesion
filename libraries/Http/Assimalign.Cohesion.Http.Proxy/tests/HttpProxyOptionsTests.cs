using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;

using Shouldly;
using Xunit;

using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.Tests;

public sealed class HttpProxyOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - HttpForwarderOptions: Should default to a safe reverse-proxy configuration")]
    public void HttpForwarderOptions_Defaults_ShouldMatchDocumentation()
    {
        // Arrange / Act
        HttpForwarderOptions options = new();

        // Assert
        options.ForwardedHeaderAction.ShouldBe(HttpForwardedHeaderAction.Set);
        options.ForwardedHeaderFormat.ShouldBe(HttpForwardedHeaderFormat.XForwarded);
        options.PreserveHost.ShouldBeFalse();
        options.AddViaToResponses.ShouldBeFalse();
        options.ActivityTimeout.ShouldBe(TimeSpan.FromSeconds(100));
        options.Version.ShouldBe(NetHttpVersion.Version20);
        options.VersionPolicy.ShouldBe(HttpVersionPolicy.RequestVersionOrLower);
        options.MaxBufferedResponseBodySize.ShouldBe(4 * 1024 * 1024);
        Regex.IsMatch(options.ViaPseudonym.ShouldNotBeNull(), "^cohesion-[0-9a-f]{8}$").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - HttpForwarderOptions: Should choose a pseudonym per instance")]
    public void HttpForwarderOptions_TwoInstances_ShouldHaveDifferentPseudonyms()
    {
        // Arrange / Act
        string? first = new HttpForwarderOptions().ViaPseudonym;
        string? second = new HttpForwarderOptions().ViaPseudonym;

        // Assert
        first.ShouldNotBe(second);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - HttpForwarderOptions: Should reject a pseudonym that is not a token")]
    [InlineData("")]
    [InlineData("edge 1")]
    [InlineData("edge,1")]
    [InlineData("edge(1)")]
    public void ViaPseudonym_NotToken_ShouldThrow(string value)
    {
        // Arrange
        HttpForwarderOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentException>(() => options.ViaPseudonym = value);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - HttpForwarderOptions: Should accept a null pseudonym to disable Via")]
    public void ViaPseudonym_Null_ShouldDisableVia()
    {
        // Arrange
        HttpForwarderOptions options = new();

        // Act
        options.ViaPseudonym = null;

        // Assert
        options.ViaPseudonym.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - HttpForwarderOptions: Should reject an activity timeout a timer cannot schedule")]
    [InlineData(0)]
    [InlineData(-1000)]
    [InlineData(50L * 24 * 60 * 60 * 1000)]
    public void ActivityTimeout_OutOfRange_ShouldThrow(long milliseconds)
    {
        // Arrange
        HttpForwarderOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.ActivityTimeout = TimeSpan.FromMilliseconds(milliseconds));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - HttpForwarderOptions: Should accept an infinite activity timeout")]
    public void ActivityTimeout_Infinite_ShouldBeAccepted()
    {
        // Arrange
        HttpForwarderOptions options = new();

        // Act
        options.ActivityTimeout = Timeout.InfiniteTimeSpan;

        // Assert
        options.ActivityTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - HttpForwarderOptions: Should reject undefined enumeration values")]
    public void EnumerationProperties_Undefined_ShouldThrow()
    {
        // Arrange
        HttpForwarderOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.ForwardedHeaderAction = (HttpForwardedHeaderAction)7);
        Should.Throw<ArgumentOutOfRangeException>(() => options.ForwardedHeaderFormat = (HttpForwardedHeaderFormat)7);
        Should.Throw<ArgumentOutOfRangeException>(() => options.VersionPolicy = (HttpVersionPolicy)7);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - HttpForwarderOptions: Should reject a null version and a negative buffer limit")]
    public void VersionAndBufferLimit_Invalid_ShouldThrow()
    {
        // Arrange
        HttpForwarderOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.Version = null!);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxBufferedResponseBodySize = -1);
        options.MaxBufferedResponseBodySize = 0;
        options.MaxBufferedResponseBodySize.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - HttpTunnelOptions: Should default to a bounded connect and an unbounded idle time")]
    public void HttpTunnelOptions_Defaults_ShouldMatchDocumentation()
    {
        // Arrange / Act
        HttpTunnelOptions options = new();

        // Assert
        options.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(15));
        options.IdleTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
        options.ConnectCallback.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - HttpTunnelOptions: Should reject a timeout a timer cannot schedule")]
    [InlineData(0)]
    [InlineData(-1000)]
    [InlineData(50L * 24 * 60 * 60 * 1000)]
    public void HttpTunnelOptions_TimeoutOutOfRange_ShouldThrow(long milliseconds)
    {
        // Arrange
        HttpTunnelOptions options = new();
        TimeSpan value = TimeSpan.FromMilliseconds(milliseconds);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.ConnectTimeout = value);
        Should.Throw<ArgumentOutOfRangeException>(() => options.IdleTimeout = value);
    }
}
