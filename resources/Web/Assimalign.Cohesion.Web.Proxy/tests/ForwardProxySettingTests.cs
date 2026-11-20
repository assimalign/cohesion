using System;

using Assimalign.Cohesion.Web.Proxy.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// The <c>forward-proxy</c> setting: its name and value, a whole-value <c>Set</c> that validates before
/// anything changes, and the separate connection pools for open and restricted targets.
/// </summary>
public class ForwardProxySettingTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: The forward-proxy setting's value is the rule list it was built with")]
    public void Build_WithRules_ShouldExposeTheRules()
    {
        // Arrange
        ForwardProxyBuilder builder = new();
        builder.Options.AddRule(new ForwardProxyRule { Hosts = ["*.example.com"], Ports = [443] });

        // Act
        using ForwardProxySetting setting = (ForwardProxySetting)builder.Build();

        // Assert
        setting.Name.ShouldBe("forward-proxy");
        setting.Value.Count.ShouldBe(1);
        setting.Value[0].Hosts.ShouldBe(["*.example.com"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: With no rule the forward proxy allows nothing")]
    public void Build_NoRules_ShouldDenyEveryTarget()
    {
        // Act
        using ForwardProxySetting setting = (ForwardProxySetting)new ForwardProxyBuilder().Build();

        // Assert
        setting.Value.ShouldBeEmpty();
        setting.Table.Evaluate("example.com", 443, 443).ShouldBe(ForwardProxyAccess.Denied);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: A valid rule list applies whole")]
    public void Set_ValidRules_ShouldReplaceTheRules()
    {
        // Arrange
        ForwardProxyBuilder builder = new();
        builder.Options.AddRule(new ForwardProxyRule { Hosts = ["old.test"] });
        using ForwardProxySetting setting = (ForwardProxySetting)builder.Build();

        // Act
        setting.Set([new ForwardProxyRule { Hosts = ["new.test"], Ports = [8080] }]);

        // Assert
        setting.Value.Count.ShouldBe(1);
        setting.Table.Evaluate("new.test", 8080, 80).ShouldBe(ForwardProxyAccess.Open);
        setting.Table.Evaluate("old.test", 80, 80).ShouldBe(ForwardProxyAccess.Denied);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: A rule list with one bad rule is refused whole, naming the rule")]
    public void Set_OneInvalidRule_ShouldThrowAndKeepTheRules()
    {
        // Arrange
        ForwardProxyBuilder builder = new();
        builder.Options.AddRule(new ForwardProxyRule { Hosts = ["old.test"] });
        using ForwardProxySetting setting = (ForwardProxySetting)builder.Build();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => setting.Set(
        [
            new ForwardProxyRule { Hosts = ["new.test"] },
            new ForwardProxyRule { Hosts = ["new.test"], Ports = [70000] },
        ]));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldContain("index 1", Case.Sensitive);
        setting.Table.Evaluate("old.test", 80, 80).ShouldBe(ForwardProxyAccess.Open);
        setting.Table.Evaluate("new.test", 80, 80).ShouldBe(ForwardProxyAccess.Denied);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: A null rule list is refused")]
    public void Set_Null_ShouldThrowArgumentNullException()
    {
        // Arrange
        using ForwardProxySetting setting = (ForwardProxySetting)new ForwardProxyBuilder().Build();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => setting.Set(null!)).ParamName.ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: Open and restricted targets never share a connection pool or a tunnel")]
    public void GetForwarderAndTunnel_OpenAndRestricted_ShouldBeSeparate()
    {
        // Arrange — a pooled connection opened for an exact host must not carry a wildcard-allowed request
        // past the address check, so each access level has its own pool.
        using ForwardProxySetting setting = (ForwardProxySetting)new ForwardProxyBuilder().Build();

        // Act / Assert
        setting.GetForwarder(ForwardProxyAccess.Open).ShouldNotBeSameAs(setting.GetForwarder(ForwardProxyAccess.Restricted));
        setting.GetTunnel(ForwardProxyAccess.Open).ShouldNotBeSameAs(setting.GetTunnel(ForwardProxyAccess.Restricted));
    }
}
