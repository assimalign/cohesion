using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

public class ApplicationBuilderTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationBuilder: ";

    [Fact(DisplayName = DisplayPrefix + "The builder is the root set, and its resources and child sets point back to it")]
    public void CreateBuilder_WithName_ShouldBeRootSet()
    {
        // Arrange & Act
        ApplicationBuilder builder = Application.CreateBuilder("root", ["--verbose"]);
        IApplicationResourceDefinition identity = builder.AddResource("IdentityHub", "identity");
        IApplicationSet east = builder.AddGateway("east");

        // Assert
        builder.Parent.ShouldBeNull();
        builder.Args.ShouldBe(["--verbose"]);
        identity.Set.ShouldBeSameAs(builder);
        east.Parent.ShouldBeSameAs(builder);
    }

    [Fact(DisplayName = DisplayPrefix + "Building needs no gateway, so a build step can produce the manifest")]
    public void Build_WithoutGateway_ShouldBuildModel()
    {
        // Arrange
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        builder.AddGateway("east").AddResource("Web", "orders-api");

        // Act
        Application application = builder.Build();

        // Assert
        application.Model.Name.ShouldBe(new GatewayName("root"));
        application.Model.Id.ShouldBe(GatewayId.FromName("root"));
        application.Model.Manifest.Gateways.ShouldHaveSingleItem().Resources.ShouldHaveSingleItem().Name.Value.ShouldBe("orders-api");
    }

    [Fact(DisplayName = DisplayPrefix + "Running without a gateway is refused")]
    public async Task RunAsync_WithoutGateway_ShouldThrow()
    {
        // Arrange
        Application application = Application.CreateBuilder("root", []).Build();

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => application.RunAsync(CancellationToken.None));
    }

    [Fact(DisplayName = DisplayPrefix + "Running hands the built model to the gateway, once")]
    public async Task RunAsync_WithGateway_ShouldRunGatewayOnce()
    {
        // Arrange
        var gateway = new RecordingGateway();
        Application application = Application.CreateBuilder("root", []).UseGateway(gateway).Build();

        // Act
        await application.RunAsync(CancellationToken.None);

        // Assert
        gateway.Model.ShouldBeSameAs(application.Model);
        await Should.ThrowAsync<InvalidOperationException>(() => application.RunAsync(CancellationToken.None));
        gateway.Runs.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Building closes composition everywhere in the hierarchy")]
    public void Build_ThenCompose_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationSet east = builder.AddGateway("east");
        IApplicationResourceDefinition database = east.AddResource("Database.Sql", "orders-db");
        IApplicationResourceDefinition api = east.AddResource("Web", "orders-api");
        builder.Build();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.AddResource("Web", "customers-api"));
        Should.Throw<InvalidOperationException>(() => east.AddGateway("east-edge"));
        Should.Throw<InvalidOperationException>(() => api.DependsOn(database, "data.read"));
        Should.Throw<InvalidOperationException>(() => builder.UseGateway(new RecordingGateway()));
        Should.Throw<InvalidOperationException>(() => builder.Build());
        api.Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A builder takes one gateway")]
    public void UseGateway_Twice_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder builder = Application.CreateBuilder("root", []).UseGateway(new RecordingGateway());

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.UseGateway(new RecordingGateway()));
    }

    [Fact(DisplayName = DisplayPrefix + "The arguments are copied")]
    public void CreateBuilder_WithArguments_ShouldCopyThem()
    {
        // Arrange
        string[] args = ["--mode", "run"];

        // Act
        ApplicationBuilder builder = Application.CreateBuilder("root", args);
        args[1] = "changed";

        // Assert
        builder.Args.ShouldBe(["--mode", "run"]);
    }
}
