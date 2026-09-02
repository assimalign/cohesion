using System;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

public class ServiceProviderBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Services: Should not be shared between builders")]
    public void Services_ForSeparateBuilders_ShouldNotShareAContainer()
    {
        // Arrange
        var first = new ServiceProviderBuilder();
        var second = new ServiceProviderBuilder();

        // Act
        first.AddSingleton<IMarkerService>(new FirstService());

        // Assert
        first.Services.ShouldNotBeSameAs(second.Services);
        first.Services.Count.ShouldBe(1);
        second.Services.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Services: Should return the same container for the same builder")]
    public void Services_OnRepeatedAccess_ShouldReturnTheSameContainer()
    {
        // Arrange
        var builder = new ServiceProviderBuilder();

        // Act
        var services = builder.Services;

        // Assert
        builder.Services.ShouldBeSameAs(services);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Build: Should resolve only the owning builder's registrations")]
    public void Build_ForSeparateBuilders_ShouldResolveOwnRegistrations()
    {
        // Arrange
        var first = new ServiceProviderBuilder();
        var second = new ServiceProviderBuilder();

        first.AddSingleton<IMarkerService>(new FirstService());
        second.AddSingleton<IMarkerService>(new SecondService());

        // Act
        var firstProvider = ((IServiceProviderBuilder)first).Build();
        var secondProvider = ((IServiceProviderBuilder)second).Build();

        // Assert
        firstProvider.GetRequiredService<IMarkerService>().ShouldBeOfType<FirstService>();
        secondProvider.GetRequiredService<IMarkerService>().ShouldBeOfType<SecondService>();
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should not throw")]
    public void Dispose_OnBuilderWithRegistrations_ShouldNotThrow()
    {
        // Arrange
        var builder = new ServiceProviderBuilder();

        builder.AddSingleton<IMarkerService>(new FirstService());

        // Act & Assert
        Should.NotThrow(builder.Dispose);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should not throw when called more than once")]
    public void Dispose_WhenCalledMoreThanOnce_ShouldNotThrow()
    {
        // Arrange
        var builder = new ServiceProviderBuilder();

        builder.Dispose();

        // Act & Assert
        Should.NotThrow(builder.Dispose);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should not throw when disposed by a using block")]
    public void Dispose_WithinAUsingBlock_ShouldNotThrow()
    {
        // Act & Assert
        Should.NotThrow(() =>
        {
            using var builder = new ServiceProviderBuilder();

            builder.AddSingleton<IMarkerService>(new FirstService());
        });
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should not affect an already built provider")]
    public void Dispose_AfterBuild_ShouldNotAffectTheBuiltProvider()
    {
        // Arrange
        IServiceProvider provider;

        // Act
        using (var builder = new ServiceProviderBuilder())
        {
            builder.AddSingleton<IMarkerService>(new FirstService());

            provider = ((IServiceProviderBuilder)builder).Build();
        }

        // Assert
        provider.GetRequiredService<IMarkerService>().ShouldBeOfType<FirstService>();
    }

    private interface IMarkerService
    {
    }

    private sealed class FirstService : IMarkerService
    {
    }

    private sealed class SecondService : IMarkerService
    {
    }
}
