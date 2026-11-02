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

namespace Assimalign.Cohesion.NotificationHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// <c>builder.Services.AddResource(...)</c> is a registration: a managed resource is one lifecycle
/// service the application's container creates when the application is built, and an unmanaged one is
/// nothing at all.
/// </summary>
public sealed class NotificationHubResourceRegistrationTests
{
    private const string DisplayPrefix = "Cohesion Test [NotificationHub.Hosting.ApplicationModel] - Registration: ";

    [Fact(DisplayName = DisplayPrefix + "An unmanaged resource registers nothing")]
    public void AddResource_WithoutPlatform_ShouldRegisterNothing()
    {
        // Arrange
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder([]);
        int registrations = builder.Services.Container.Count();

        // Act
        builder.Services.AddResource("notifications", resource => resource.Platform = null);

        // Assert
        builder.Services.Container.Count().ShouldBe(registrations);
    }

    [Fact(DisplayName = DisplayPrefix + "A managed resource registers one lifecycle service")]
    public void AddResource_WithPlatform_ShouldRegisterOneLifecycleService()
    {
        // Arrange
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder([]);

        // Act
        builder.Services.AddResource("notifications", resource =>
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
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder([]);
        builder.Services.AddSingleton(new Greeting("from the application"));
        builder.Services.AddResource("notifications", resource =>
        {
            resource.Platform = "test";
            resource.UseGatewayClient(new TestGatewayClient());
            resource.AddAction(services => new GreetingAction(services.GetRequiredService<Greeting>().Text), GreetingJsonContext.Default);
        });

        // Act
        await using NotificationHubApplication application = builder.Build();

        // Assert
        IApplicationInstanceService service = application.Context.ServiceProvider
            .GetRequiredService<IEnumerable<IHostService>>()
            .OfType<IApplicationInstanceService>()
            .ShouldHaveSingleItem();
        IApplicationInstance instance = service.ControlPlane.Instance;
        instance.Provider.ShouldBe((ProviderName)"NotificationHub");
        instance.Name.ShouldBe((ResourceName)"notifications");
        instance.HealthContributors.ShouldBeEmpty();
        instance.Actions.ShouldHaveSingleItem().Name.ShouldBe((ActionName)"greeting");
    }

    [Fact(DisplayName = DisplayPrefix + "A platform the resource has no client for fails the registration")]
    public void AddResource_WithPlatformWithoutClient_ShouldThrow()
    {
        // Arrange
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder([]);

        // Act
        Action act = () => builder.Services.AddResource("notifications", resource => resource.Platform = "test");

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("'test'", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A default name or missing registrations are refused")]
    public void AddResource_WithDefaultNameOrNullServices_ShouldThrow()
    {
        // Arrange
        NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder([]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.Services.AddResource(default));
        Should.Throw<ArgumentNullException>(() => ((IServiceProviderBuilder)null!).AddResource("notifications"));
    }
}
