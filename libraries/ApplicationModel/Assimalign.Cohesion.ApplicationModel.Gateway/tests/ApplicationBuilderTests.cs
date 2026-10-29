using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ApplicationBuilderTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationBuilder: ";

    [Fact(DisplayName = DisplayPrefix + "One builder declares the model and builds the gateway that runs it")]
    public void Build_WithModelAndInfrastructure_ShouldBuildTheGateway()
    {
        // Arrange
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("root");
        IApplicationResourceDefinition identity = builder.AddResource("IdentityHub", "identity");
        builder.StartupTimeout = TimeSpan.FromSeconds(30);
        builder.LeaseDuration = TimeSpan.FromSeconds(10);

        // Act
        ApplicationGateway gateway = builder.UseInfrastructure(new TestInfrastructure()).Build();

        // Assert
        builder.Resources.ShouldHaveSingleItem().ShouldBeSameAs(identity);
        identity.Set.Name.ShouldBe(builder.Name);
        gateway.Name.ShouldBe(new GatewayName("root"));
        gateway.Model.Manifest.Resources.ShouldHaveSingleItem().Name.Value.ShouldBe("identity");
        gateway.Model.Role.ShouldBe(ApplicationGatewayRole.Root);
        gateway.StartupTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        gateway.LeaseDuration.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway needs an infrastructure provider")]
    public void Build_WithoutInfrastructure_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("root");

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message.ShouldContain("UseInfrastructure", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A parent named only in the model has no connection, so building is refused")]
    public void Build_WithParentNamedOnlyInTheModel_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("east");
        ((IApplicationModelBuilder)builder).UseParent("root");
        builder.UseInfrastructure(new TestInfrastructure());

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message.ShouldContain("call UseParent", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A model built through the model builder is the one the gateway runs")]
    public void Build_AfterModelBuild_ShouldRunThatModel()
    {
        // Arrange
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        IApplicationModel model = ((IApplicationModelBuilder)builder).Build();

        // Act
        ApplicationGateway gateway = builder.UseInfrastructure(new TestInfrastructure()).Build();

        // Assert
        gateway.Model.ShouldBeSameAs(model);
        Should.Throw<InvalidOperationException>(() => builder.AddResource("Web", "customers-api"));
    }

    [Fact(DisplayName = DisplayPrefix + "Building closes the builder")]
    public void Build_ThenChange_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("root").UseInfrastructure(new TestInfrastructure());
        builder.Build();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
        Should.Throw<InvalidOperationException>(() => builder.UseInfrastructure(new TestInfrastructure()));
        Should.Throw<InvalidOperationException>(() => builder.StartupTimeout = TimeSpan.FromSeconds(1));
        Should.Throw<InvalidOperationException>(() => builder.AddResource("Web", "orders-api"));
    }

    [Fact(DisplayName = DisplayPrefix + "Time limits must be positive")]
    public void StartupTimeoutAndLeaseDuration_WithNonPositiveValue_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder builder = ApplicationGateway.CreateBuilder("root");

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => builder.StartupTimeout = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => builder.LeaseDuration = TimeSpan.FromSeconds(-1));
    }

    [Fact(DisplayName = DisplayPrefix + "A child built with its parent joins it when it runs and reads its outputs")]
    public async Task UseParent_WithGateway_ShouldJoinItWhenRun()
    {
        // Arrange: each gateway is declared and built on its own, as two programs would.
        var rootInfrastructure = new TestInfrastructure();
        ApplicationBuilder rootBuilder = ApplicationGateway.CreateBuilder("root");
        rootBuilder.AddResource("IdentityHub", "identity");
        ApplicationGateway root = rootBuilder.UseInfrastructure(rootInfrastructure).Build();
        rootInfrastructure.Gateway = root;

        var eastInfrastructure = new TestInfrastructure();
        ApplicationBuilder eastBuilder = ApplicationGateway.CreateBuilder("east");
        IApplicationParent parent = eastBuilder.UseParent(root);
        eastBuilder.AddResource("Web", "orders-api").WithSetting("Identity:Authority", parent.Endpoint("IdentityHub", "identity"));
        ApplicationGateway east = eastBuilder.UseInfrastructure(eastInfrastructure).Build();
        eastInfrastructure.Gateway = east;
        using var stop = new CancellationTokenSource();

        // Act
        Task rootRun = root.RunAsync(stop.Token);
        Task eastRun = east.RunAsync(stop.Token);
        await Task.WhenAll(root.Started, east.Started).WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Task.WhenAll(rootRun, eastRun);

        // Assert
        parent.Name.ShouldBe(root.Name);
        east.Model.Parent.ShouldBe(new GatewayName("root"));
        TestInstance api = eastInfrastructure.Instances.Values.Select(entry => entry.Instance).Single();
        api.Settings["Identity:Authority"].ShouldBe("http://identity.root.svc");
    }
}
