using System;

using Assimalign.Cohesion.Web.Proxy.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// Rule evaluation: no rule, no access; a rule with no ports allows the scheme's default port; an exact
/// host opens the target, and a wildcard pattern allows it under the address policy.
/// </summary>
public class ForwardProxyRuleTableTests
{
    private const int httpPort = 80;
    private const int httpsPort = 443;

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Rule table: With no rule nothing is allowed")]
    public void Evaluate_NoRules_ShouldDeny()
    {
        // Arrange
        ForwardProxyRuleTable table = ForwardProxyRuleTable.Create([], "rules");

        // Act
        ForwardProxyAccess access = table.Evaluate("example.com", httpPort, httpPort);

        // Assert
        table.Rules.ShouldBeEmpty();
        access.ShouldBe(ForwardProxyAccess.Denied);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Rule table: A rule with no ports allows only the scheme's default port")]
    [InlineData(httpPort, httpPort, "Open")]
    [InlineData(httpsPort, httpsPort, "Open")]
    [InlineData(httpsPort, httpPort, "Denied")]
    [InlineData(httpPort, httpsPort, "Denied")]
    [InlineData(8080, httpPort, "Denied")]
    [InlineData(22, httpsPort, "Denied")]
    public void Evaluate_RuleWithoutPorts_ShouldAllowTheDefaultPortOnly(int port, int defaultPort, string expected)
    {
        // Arrange
        ForwardProxyRuleTable table = ForwardProxyRuleTable.Create([new ForwardProxyRule { Hosts = ["example.com"] }], "rules");

        // Act
        ForwardProxyAccess access = table.Evaluate("example.com", port, defaultPort);

        // Assert
        access.ShouldBe(Enum.Parse<ForwardProxyAccess>(expected));
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Rule table: A rule with ports allows exactly those ports")]
    [InlineData(8080, "Open")]
    [InlineData(8443, "Open")]
    [InlineData(httpPort, "Denied")]
    [InlineData(httpsPort, "Denied")]
    public void Evaluate_RuleWithPorts_ShouldAllowThosePortsOnly(int port, string expected)
    {
        // Arrange
        ForwardProxyRuleTable table = ForwardProxyRuleTable.Create(
            [new ForwardProxyRule { Hosts = ["example.com"], Ports = [8080, 8443] }], "rules");

        // Act
        ForwardProxyAccess access = table.Evaluate("example.com", port, port == httpsPort ? httpsPort : httpPort);

        // Assert
        access.ShouldBe(Enum.Parse<ForwardProxyAccess>(expected));
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Rule table: An exact host opens the target and a wildcard restricts it")]
    [InlineData("api.example.com", "Open")]
    [InlineData("API.EXAMPLE.COM", "Open")]
    [InlineData("api.example.com:80", "Open")]
    [InlineData("www.example.com", "Restricted")]
    [InlineData("a.b.example.com", "Restricted")]
    [InlineData("example.com", "Denied")]
    [InlineData("evilexample.com", "Denied")]
    [InlineData("10.0.0.5", "Open")]
    [InlineData("[2001:db8::5]", "Open")]
    [InlineData("other.test", "Denied")]
    public void Evaluate_ExactAndWildcardPatterns_ShouldOpenOrRestrict(string host, string expected)
    {
        // Arrange
        ForwardProxyRuleTable table = ForwardProxyRuleTable.Create(
            [new ForwardProxyRule { Hosts = ["api.example.com", "*.example.com", "10.0.0.5", "[2001:db8::5]"] }], "rules");

        // Act
        ForwardProxyAccess access = table.Evaluate(host, httpPort, httpPort);

        // Assert
        access.ShouldBe(Enum.Parse<ForwardProxyAccess>(expected));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Rule table: '*' allows any host under the address policy")]
    public void Evaluate_MatchAnyPattern_ShouldRestrict()
    {
        // Arrange
        ForwardProxyRuleTable table = ForwardProxyRuleTable.Create([new ForwardProxyRule { Hosts = ["*"], Ports = [httpsPort] }], "rules");

        // Act
        ForwardProxyAccess named = table.Evaluate("anything.test", httpsPort, httpsPort);
        ForwardProxyAccess literal = table.Evaluate("127.0.0.1", httpsPort, httpsPort);

        // Assert
        named.ShouldBe(ForwardProxyAccess.Restricted);
        literal.ShouldBe(ForwardProxyAccess.Restricted);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Rule table: The most open access any rule grants wins")]
    public void Evaluate_SeveralRules_ShouldGrantTheMostOpenAccess()
    {
        // Arrange — the wildcard rule comes first, the exact rule for the same host later.
        ForwardProxyRuleTable table = ForwardProxyRuleTable.Create(
        [
            new ForwardProxyRule { Hosts = ["*"], Ports = [8080] },
            new ForwardProxyRule { Hosts = ["intranet.test"], Ports = [8080] },
        ], "rules");

        // Act
        ForwardProxyAccess exact = table.Evaluate("intranet.test", 8080, httpPort);
        ForwardProxyAccess other = table.Evaluate("other.test", 8080, httpPort);
        ForwardProxyAccess wrongPort = table.Evaluate("intranet.test", 9090, httpPort);

        // Assert
        exact.ShouldBe(ForwardProxyAccess.Open);
        other.ShouldBe(ForwardProxyAccess.Restricted);
        wrongPort.ShouldBe(ForwardProxyAccess.Denied);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Rule table: A null rule in a value is refused with its index")]
    public void Create_NullRule_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ForwardProxyRuleTable.Create([new ForwardProxyRule { Hosts = ["*"] }, null!], "value"));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldContain("index 1", Case.Sensitive);
    }
}
