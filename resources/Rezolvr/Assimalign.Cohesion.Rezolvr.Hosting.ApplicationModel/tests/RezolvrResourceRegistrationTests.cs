using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Rezolvr.Hosting.ApplicationModel.Tests;

/// <summary>
/// <c>builder.Services.AddResource(...)</c> is a registration: a managed resource is one lifecycle
/// service the application's container creates when the application is built, and an unmanaged one is
/// nothing at all.
/// </summary>
public sealed class RezolvrResourceRegistrationTests
{
    private const string DisplayPrefix = "Cohesion Test [Rezolvr.Hosting.ApplicationModel] - Registration: ";

    [Fact(DisplayName = DisplayPrefix + "An unmanaged resource registers nothing")]
    public void AddResource_WithoutPlatform_ShouldRegisterNothing()
    {
        // Arrange
        RezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder([]);
        int registrations = builder.Services.Container.Count();

        // Act
        builder.Services.AddResource("resolver", resource => resource.Platform = null);

        // Assert
        builder.Services.Container.Count().ShouldBe(registrations);
    }

    [Fact(DisplayName = DisplayPrefix + "A managed resource registers one lifecycle service")]
    public void AddResource_WithPlatform_ShouldRegisterOneLifecycleService()
    {
        // Arrange
        RezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder([]);

        // Act
        builder.Services.AddResource("resolver", resource =>
        {
            resource.Platform = "test";
            resource.UseGatewayClient(new TestGatewayClient());
        });

        // Assert
        ServiceDescriptor registration = builder.Services.Container.Single(descriptor => descriptor.ServiceType == typeof(IHostService));
        registration.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact(DisplayName = DisplayPrefix + "Building the application creates the resource with the actions the application added")]
    public async Task Build_WithManagedResource_ShouldCreateTheInstanceServiceFromTheContainer()
    {
        // Arrange
        RezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder([]);
        builder.Services.AddSingleton(new Greeting("from the application"));
        builder.Services.AddResource("resolver", resource =>
        {
            resource.Platform = "test";
            resource.UseGatewayClient(new TestGatewayClient());
            resource.AddAction(services => new GreetingAction(services.GetRequiredService<Greeting>().Text), GreetingJsonContext.Default);
        });

        // Act
        await using RezolvrApplication application = builder.Build();

        // Assert
        IApplicationInstanceService service = application.Context.ServiceProvider
            .GetRequiredService<IEnumerable<IHostService>>()
            .OfType<IApplicationInstanceService>()
            .ShouldHaveSingleItem();
        IApplicationInstance instance = service.ControlPlane.Instance;
        instance.Provider.ShouldBe((ProviderName)"Rezolvr");
        instance.Name.ShouldBe((ResourceName)"resolver");
        instance.HealthContributors.ShouldBeEmpty();
        instance.Actions.ShouldHaveSingleItem().Name.ShouldBe((ActionName)"greeting");
    }

    [Fact(DisplayName = DisplayPrefix + "A platform the resource has no client for fails the registration")]
    public void AddResource_WithPlatformWithoutClient_ShouldThrow()
    {
        // Arrange
        RezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder([]);

        // Act
        Action act = () => builder.Services.AddResource("resolver", resource => resource.Platform = "test");

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("'test'", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A default name or missing registrations are refused")]
    public void AddResource_WithDefaultNameOrNullServices_ShouldThrow()
    {
        // Arrange
        RezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder([]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.Services.AddResource(default));
        Should.Throw<ArgumentNullException>(() => ((IServiceProviderBuilder)null!).AddResource("resolver"));
    }
}
