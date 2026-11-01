using System;
using System.Collections.Generic;
using System.Threading;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class ApplicationInstanceOptionsTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ApplicationInstanceOptions: ";

    private static readonly Dictionary<string, string> _none = [];

    [Fact(DisplayName = DisplayPrefix + "The platform and the deployment default to the instance's environment")]
    public void Create_InEnvironment_ShouldReadThePlatformAndTheDeployment()
    {
        // Arrange
        DeploymentId deployment = DeploymentId.New();

        // Act
        TestOptions options = CreateIn(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "inprocess",
            [AppEnvironment.Variables.Deployment] = deployment.ToString(),
        });

        // Assert
        options.Platform.ShouldBe((PlatformName)"inprocess");
        options.Deployment.ShouldBe(deployment);
    }

    [Fact(DisplayName = DisplayPrefix + "An environment that names no platform runs the instance unmanaged")]
    public void GetGatewayClient_WithoutPlatform_ShouldReturnNull()
    {
        // Arrange
        TestOptions options = CreateIn(_none);
        options.UseGatewayClient(new TestGatewayClient());

        // Act & Assert
        options.Platform.ShouldBeNull();
        options.Deployment.ShouldBeNull();
        options.GetGatewayClient().ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "The client of the platform the environment names is the one selected")]
    public void GetGatewayClient_WithSeveralPlatforms_ShouldSelectTheNamedOne()
    {
        // Arrange
        var inProcess = new TestGatewayClient { Platform = "inprocess" };
        var kubernetes = new TestGatewayClient { Platform = "kubernetes" };
        TestOptions options = CreateIn(_none);
        options.UseGatewayClient(inProcess).UseGatewayClient(kubernetes);

        // Act
        options.Platform = "kubernetes";
        IApplicationInstanceGatewayClient? selected = options.GetGatewayClient();

        // Assert
        selected.ShouldBeSameAs(kubernetes);
        options.GatewayClients.ShouldBe([inProcess, kubernetes]);
    }

    [Fact(DisplayName = DisplayPrefix + "A platform the resource uses no client for fails the build, naming the platform")]
    public void GetGatewayClient_WithoutClientForPlatform_ShouldThrow()
    {
        // Arrange
        TestOptions options = CreateIn(_none);
        options.UseGatewayClient(new TestGatewayClient { Platform = "inprocess" });
        options.Platform = "kubernetes";

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => options.GetGatewayClient());

        // Assert
        exception.Message.ShouldContain("'kubernetes'", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A resource uses at most one client per platform")]
    public void UseGatewayClient_WithSecondClientForPlatform_ShouldThrow()
    {
        // Arrange
        TestOptions options = CreateIn(_none);
        options.UseGatewayClient(new TestGatewayClient { Platform = "inprocess" });

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.UseGatewayClient(new TestGatewayClient { Platform = "inprocess" }));
        Should.Throw<ArgumentException>(() => options.UseGatewayClient(new TestGatewayClient { Platform = default }));
        Should.Throw<ArgumentNullException>(() => options.UseGatewayClient(null!));
        options.GatewayClients.ShouldHaveSingleItem();
    }

    [Theory(DisplayName = DisplayPrefix + "An environment that names a platform or a deployment wrongly fails at once")]
    [InlineData("LoCaL", null)]
    [InlineData("inprocess", "not-a-deployment")]
    public void Create_WithMalformedEnvironment_ShouldThrow(string platform, string? deployment)
    {
        // Arrange
        var variables = new Dictionary<string, string> { [AppEnvironment.Variables.Gateway] = platform };
        if (deployment is not null)
        {
            variables[AppEnvironment.Variables.Deployment] = deployment;
        }

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => CreateIn(variables));
    }

    // The options read the environment of the scope they are created in, as an instance a gateway
    // runs in its own process reads its own.
    private static TestOptions CreateIn(IReadOnlyDictionary<string, string> variables)
    {
        using (AppEnvironment.Enter(AppEnvironmentScope.Create(variables, CancellationToken.None)))
        {
            return new TestOptions();
        }
    }
}
