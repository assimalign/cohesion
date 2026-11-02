using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Tests;

/// <summary>
/// <c>builder.Services.AddResource(...)</c> is a registration: a managed resource is one lifecycle
/// service the application's container creates when the application is built, and an unmanaged one is
/// nothing at all.
/// </summary>
public sealed class WebResourceRegistrationTests
{
    private const string DisplayPrefix = "Cohesion Test [Web.Hosting.ApplicationModel] - Registration: ";

    [Fact(DisplayName = DisplayPrefix + "An unmanaged resource registers nothing")]
    public void AddResource_WithoutPlatform_ShouldRegisterNothing()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        int registrations = builder.Services.Container.Count();

        // Act
        builder.Services.AddResource("orders-api", resource => resource.Platform = null);

        // Assert
        builder.Services.Container.Count().ShouldBe(registrations);
    }

    [Fact(DisplayName = DisplayPrefix + "A managed resource registers one lifecycle service")]
    public void AddResource_WithPlatform_ShouldRegisterOneLifecycleService()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());

        // Act
        builder.Services.AddResource("orders-api", resource =>
        {
            resource.Platform = "test";
            resource.UseGatewayClient(new TestGatewayClient());
        });

        // Assert
        ServiceDescriptor registration = builder.Services.Container.Single(descriptor => descriptor.ServiceType == typeof(IHostService));
        registration.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact(DisplayName = DisplayPrefix + "Building the application creates the resource from the container")]
    public async Task Build_WithManagedResource_ShouldCreateTheInstanceServiceFromTheContainer()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.Services.AddResource("orders-api", resource =>
        {
            resource.Platform = "test";
            resource.UseGatewayClient(new TestGatewayClient());
        });

        // Act
        WebApplication application = builder.Build();
        await using var _ = (IAsyncDisposable)application;

        // Assert
        IApplicationInstanceService instance = application.Context.ServiceProvider
            .GetRequiredService<IEnumerable<IHostService>>()
            .OfType<IApplicationInstanceService>()
            .ShouldHaveSingleItem();
        instance.ControlPlane.Instance.Name.Value.ShouldBe("orders-api");
    }

    [Fact(DisplayName = DisplayPrefix + "A platform the resource has no client for fails the registration")]
    public void AddResource_WithPlatformWithoutClient_ShouldThrow()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());

        // Act
        Action act = () => builder.Services.AddResource("orders-api", resource => resource.Platform = "test");

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }
}
