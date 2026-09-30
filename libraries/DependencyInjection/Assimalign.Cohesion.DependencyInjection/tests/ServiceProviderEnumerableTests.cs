using System;
using System.Linq;

using Shouldly;
using Xunit;

using static Assimalign.Cohesion.DependencyInjection.Tests.GenericServiceFixtures;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies that single and enumerable resolution agree when a service has closed and open generic
/// registrations.
/// </summary>
public sealed class ServiceProviderEnumerableTests
{
    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetServices: Should resolve closed and open generic registrations in either resolution order")]
    [InlineData(false)]
    [InlineData(true)]
    public void GetServices_WithClosedAndOpenGenericRegistrations_ShouldResolveEachRegistration(bool enumerateFirst)
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddTransient<IGenericService<Argument>, ClosedGenericService>();
            builder.AddTransient(typeof(IGenericService<>), typeof(OpenGenericService<>));
        });

        // Act
        (object single, Type[] all) = Resolve(provider, enumerateFirst);

        // Assert
        single.ShouldBeOfType<ClosedGenericService>();
        all.ShouldBe([typeof(ClosedGenericService), typeof(OpenGenericService<Argument>)]);
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetServices: Should share each singleton with GetService in either resolution order")]
    [InlineData(false)]
    [InlineData(true)]
    public void GetServices_WithClosedAndOpenGenericSingletons_ShouldShareInstancesWithGetService(bool enumerateFirst)
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddSingleton<IGenericService<Argument>, ClosedGenericService>();
            builder.AddSingleton(typeof(IGenericService<>), typeof(OpenGenericService<>));
        });

        // Act
        IGenericService<Argument>[] all;
        IGenericService<Argument> single;
        if (enumerateFirst)
        {
            all = provider.GetServices<IGenericService<Argument>>().ToArray();
            single = provider.GetRequiredService<IGenericService<Argument>>();
        }
        else
        {
            single = provider.GetRequiredService<IGenericService<Argument>>();
            all = provider.GetServices<IGenericService<Argument>>().ToArray();
        }

        // Assert
        all.Length.ShouldBe(2);
        all[0].ShouldBeSameAs(single);
        all[1].ShouldBeOfType<OpenGenericService<Argument>>();
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetServices: Should list mixed registrations in declaration order")]
    public void GetServices_WithOpenGenericRegisteredFirst_ShouldListRegistrationsInDeclarationOrder()
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddTransient(typeof(IGenericService<>), typeof(OpenGenericService<>));
            builder.AddTransient<IGenericService<Argument>, ClosedGenericService>();
        });

        // Act
        (object single, Type[] all) = Resolve(provider, enumerateFirst: true);

        // Assert
        single.ShouldBeOfType<ClosedGenericService>();
        all.ShouldBe([typeof(OpenGenericService<Argument>), typeof(ClosedGenericService)]);
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should report the last registration's constraint violation in either resolution order")]
    [InlineData(false)]
    [InlineData(true)]
    public void GetService_WhenLastOpenGenericRejectsTheArgument_ShouldThrowInEitherOrder(bool enumerateFirst)
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddTransient(typeof(IGenericService<>), typeof(OpenGenericService<>));
            builder.AddTransient(typeof(IGenericService<>), typeof(ConstrainedGenericService<>));
        });

        if (enumerateFirst)
        {
            provider.GetServices<IGenericService<Argument>>()
                .Select(service => service.GetType())
                .ShouldBe([typeof(OpenGenericService<Argument>)]);
        }

        // Act & Assert
        Should.Throw<ArgumentException>(() => provider.GetService(typeof(IGenericService<Argument>)));
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should skip an earlier open generic that rejects the argument")]
    public void GetService_WhenEarlierOpenGenericRejectsTheArgument_ShouldResolveTheLastRegistration()
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddTransient(typeof(IGenericService<>), typeof(ConstrainedGenericService<>));
            builder.AddTransient(typeof(IGenericService<>), typeof(OpenGenericService<>));
        });

        // Act
        (object single, Type[] all) = Resolve(provider, enumerateFirst: true);

        // Assert
        single.ShouldBeOfType<OpenGenericService<Argument>>();
        all.ShouldBe([typeof(OpenGenericService<Argument>)]);
    }

    private static ServiceProvider Build(Action<ServiceProviderBuilder> configure)
    {
        using var builder = new ServiceProviderBuilder();
        configure(builder);
        return (ServiceProvider)((IServiceProviderBuilder)builder).Build();
    }

    private static (object Single, Type[] All) Resolve(ServiceProvider provider, bool enumerateFirst)
    {
        if (enumerateFirst)
        {
            Type[] all = provider.GetServices<IGenericService<Argument>>().Select(service => service.GetType()).ToArray();
            return (provider.GetRequiredService<IGenericService<Argument>>(), all);
        }

        object single = provider.GetRequiredService<IGenericService<Argument>>();
        return (single, provider.GetServices<IGenericService<Argument>>().Select(service => service.GetType()).ToArray());
    }
}
