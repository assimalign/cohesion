using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

/// <summary>
/// <c>builder.Services.AddResource(...)</c> is a registration: a managed resource is one lifecycle
/// service the application creates from its container registration at Build, and an unmanaged one is
/// nothing at all.
/// </summary>
public sealed class DatabaseResourceRegistrationTests
{
    private const string displayPrefix = "Cohesion Test [Database.Hosting.ApplicationModel] - Registration: ";

    [Fact(DisplayName = displayPrefix + "An unmanaged resource registers nothing")]
    public void AddResource_WithoutPlatform_ShouldRegisterNothing()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder([]);
        int registrations = builder.Services.Container.Count();

        // Act
        builder.Services.AddResource("orders-db", resource => resource.Platform = null);

        // Assert
        builder.Services.Container.Count().ShouldBe(registrations);
    }

    [Fact(DisplayName = displayPrefix + "A managed resource registers one lifecycle service")]
    public void AddResource_WithPlatform_ShouldRegisterOneLifecycleService()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder([]);

        // Act
        builder.Services.AddResource("orders-db", resource =>
        {
            resource.Platform = "test";
            resource.UseGatewayClient(new TestGatewayClient());
        });

        // Assert
        ServiceDescriptor registration = builder.Services.Container.Single(descriptor => descriptor.ServiceType == typeof(IHostService));
        registration.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact(DisplayName = displayPrefix + "Building the application creates the resource with the actions the application added")]
    public async Task Build_WithManagedResource_ShouldIncludeTheInstanceInHostedServices()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder([]);
        builder.Services.AddSingleton(new Greeting("from the application"));
        builder.Services.AddResource("orders-db", resource =>
        {
            resource.Platform = "test";
            resource.UseGatewayClient(new TestGatewayClient());
            resource.AddAction(services => new GreetingAction(services.GetRequiredService<Greeting>().Text), GreetingJsonContext.Default);
        });

        // Act
        await using DatabaseApplication application = builder.Build();

        // Assert
        IApplicationInstanceService service = application.Context.HostedServices
            .OfType<IApplicationInstanceService>()
            .ShouldHaveSingleItem();
        IApplicationInstance instance = service.ControlPlane.Instance;
        instance.Provider.ShouldBe((ProviderName)"Database");
        instance.Name.ShouldBe((ResourceName)"orders-db");
        instance.HealthContributors.ShouldBeEmpty();
        instance.Actions.ShouldHaveSingleItem().Name.ShouldBe((ActionName)"greeting");
    }

    [Fact(DisplayName = displayPrefix + "A platform the resource has no client for fails the registration")]
    public void AddResource_WithPlatformWithoutClient_ShouldThrow()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder([]);
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(
            new Dictionary<string, string> { [AppEnvironment.Variables.Gateway] = "test" }, CancellationToken.None));

        // Act
        Action act = () => builder.Services.AddResource("orders-db");

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("'test'", Case.Sensitive);
    }

    [Fact(DisplayName = displayPrefix + "A default name or missing registrations are refused")]
    public void AddResource_WithDefaultNameOrNullServices_ShouldThrow()
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder([]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.Services.AddResource(default));
        Should.Throw<ArgumentNullException>(() => ((IServiceProviderBuilder)null!).AddResource("orders-db"));
    }
}
