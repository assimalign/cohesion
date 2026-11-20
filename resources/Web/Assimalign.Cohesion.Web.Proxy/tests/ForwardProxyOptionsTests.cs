using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// Rule validation where rules are registered, the options' defaults, and the connection settings a
/// forward proxy refuses because it must check each address it connects to.
/// </summary>
public class ForwardProxyOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: Rules are listed in the order they were added")]
    public void AddRule_ValidRules_ShouldListThemInOrder()
    {
        // Arrange
        ForwardProxyOptions options = new();

        // Act
        options
            .AddRule(new ForwardProxyRule { Hosts = ["*.example.com"] })
            .AddRule(new ForwardProxyRule { Hosts = ["intranet.test"], Ports = [8080] });

        // Assert
        options.Rules.Count.ShouldBe(2);
        options.Rules[0].Hosts.ShouldBe(["*.example.com"]);
        options.Rules[1].Ports.ShouldBe([8080]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A null rule is refused")]
    public void AddRule_Null_ShouldThrowArgumentNullException()
    {
        // Arrange
        ForwardProxyOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.AddRule(null!)).ParamName.ShouldBe("rule");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A rule that names no host is refused")]
    public void AddRule_NoHost_ShouldThrowArgumentException()
    {
        // Arrange
        ForwardProxyOptions options = new();

        // Act
        ArgumentException emptyList = Should.Throw<ArgumentException>(() => options.AddRule(new ForwardProxyRule { Hosts = [] }));
        ArgumentException nullList = Should.Throw<ArgumentException>(() => options.AddRule(new ForwardProxyRule { Hosts = null! }));

        // Assert — the message tells the operator how to allow any host on purpose.
        emptyList.ParamName.ShouldBe("rule");
        emptyList.Message.ShouldContain("'*'", Case.Sensitive);
        nullList.Message.ShouldContain("index 0", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A host pattern that is not valid is refused with the rule's index")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("example.com:443")]
    [InlineData("api.*.example.com")]
    public void AddRule_InvalidHostPattern_ShouldThrowArgumentException(string pattern)
    {
        // Arrange
        ForwardProxyOptions options = new();
        options.AddRule(new ForwardProxyRule { Hosts = ["example.com"] });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRule(new ForwardProxyRule { Hosts = ["intranet.test", pattern] }));

        // Assert
        exception.ParamName.ShouldBe("rule");
        exception.Message.ShouldContain("index 1", Case.Sensitive);
        options.Rules.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A port outside 1 to 65535 is refused")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void AddRule_PortOutOfRange_ShouldThrowArgumentException(int port)
    {
        // Arrange
        ForwardProxyOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRule(new ForwardProxyRule { Hosts = ["example.com"], Ports = [443, port] }));

        // Assert
        exception.Message.ShouldContain($"port {port}", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A null port list is refused")]
    public void AddRule_NullPorts_ShouldThrowArgumentException()
    {
        // Arrange
        ForwardProxyOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRule(new ForwardProxyRule { Hosts = ["example.com"], Ports = null! }));

        // Assert
        exception.Message.ShouldContain("null port list", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: The rule keeps a copy of its lists")]
    public void AddRule_ListsChangedAfterward_ShouldKeepTheSnapshot()
    {
        // Arrange
        ForwardProxyOptions options = new();
        List<string> hosts = ["example.com"];
        List<int> ports = [443];

        // Act
        options.AddRule(new ForwardProxyRule { Hosts = hosts, Ports = ports });
        hosts.Add("*");
        ports.Add(22);

        // Assert
        options.Rules[0].Hosts.ShouldBe(["example.com"]);
        options.Rules[0].Ports.ShouldBe([443]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: The forwarder defaults add no forwarding headers and announce the proxy in Via")]
    public void Constructor_Defaults_ShouldForwardLikeAForwardProxy()
    {
        // Act
        ForwardProxyOptions options = new();

        // Assert
        options.Rules.ShouldBeEmpty();
        options.Forwarder.ForwardedHeaderAction.ShouldBe(HttpForwardedHeaderAction.None);
        options.Forwarder.AddViaToResponses.ShouldBeTrue();
        options.Tunnel.ConnectCallback.ShouldBeNull();
        options.ConfigureHandler.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A tunnel connect callback fails the build")]
    public void Build_TunnelConnectCallback_ShouldThrowInvalidOperationException()
    {
        // Arrange
        ForwardProxyBuilder builder = new();
        builder.Options.AddRule(new ForwardProxyRule { Hosts = ["example.com"] });
        builder.Options.Tunnel.ConnectCallback = static (_, _) => ValueTask.FromResult<Stream>(Stream.Null);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("ConnectCallback", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A handler that connects for the proxy fails the build")]
    [InlineData(nameof(SocketsHttpHandler.ConnectCallback))]
    [InlineData(nameof(SocketsHttpHandler.UseProxy))]
    public void Build_HandlerConnectsForTheProxy_ShouldThrowInvalidOperationException(string property)
    {
        // Arrange
        ForwardProxyBuilder builder = new();
        builder.Options.ConfigureHandler = handler =>
        {
            if (property == nameof(SocketsHttpHandler.UseProxy))
            {
                handler.UseProxy = true;
                return;
            }

            handler.ConnectCallback = static (_, _) => ValueTask.FromResult<Stream>(Stream.Null);
        };

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain(property, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A handler that would change what the proxy relays fails the build")]
    public void Build_HandlerDecompresses_ShouldThrowInvalidOperationException()
    {
        // Arrange
        ForwardProxyBuilder builder = new();
        builder.Options.ConfigureHandler = static handler => handler.AutomaticDecompression = DecompressionMethods.All;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain(nameof(SocketsHttpHandler.AutomaticDecompression), Case.Sensitive);
    }
}
