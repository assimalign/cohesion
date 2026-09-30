using System;
using System.Threading;

using Shouldly;
using Xunit;

using static Assimalign.Cohesion.DependencyInjection.Tests.ValidationFixtures;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies scope validation: scoped services are rejected from the root provider and from
/// singletons, per registration rather than per service type, and each call site is walked once.
/// </summary>
public sealed class ServiceProviderValidationTests
{
    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should resolve a non-scoped default from root when an earlier registration is scoped")]
    [InlineData(false)]
    [InlineData(true)]
    public void GetService_WhenScopedRegistrationIsNotTheDefault_ShouldResolveTheDefaultFromRoot(bool validateOnBuild)
    {
        // Arrange
        using var provider = Build(validateOnBuild, builder =>
        {
            builder.AddScoped<IValidatedService, ScopedValidatedService>();
            builder.AddSingleton<IValidatedService, SingletonValidatedService>();
        });

        // Act
        var service = provider.GetRequiredService<IValidatedService>();

        // Assert
        service.ShouldBeOfType<SingletonValidatedService>();
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should reject a scoped default from root when an earlier registration is an instance")]
    [InlineData(false)]
    [InlineData(true)]
    public void GetService_WhenScopedDefaultFollowsAnInstanceRegistration_ShouldThrowFromRoot(bool validateOnBuild)
    {
        // Arrange
        using var provider = Build(validateOnBuild, builder =>
        {
            builder.AddSingleton<IValidatedService>(new SingletonValidatedService());
            builder.AddScoped<IValidatedService, ScopedValidatedService>();
        });

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => provider.GetService(typeof(IValidatedService)));

        // Assert
        exception.Message.ShouldContain("from root provider");
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Build: Should reject a singleton consuming a scoped default that follows an instance registration")]
    public void Build_WhenSingletonConsumesScopedDefaultAfterAnInstanceRegistration_ShouldThrow()
    {
        // Arrange: the instance registration is not the default, so it must not answer for the scoped
        // default's call site when the validator memoizes it.
        using var builder = CreateBuilder(validateOnBuild: true);
        builder.AddSingleton<IValidatedService>(new SingletonValidatedService());
        builder.AddScoped<IValidatedService, ScopedValidatedService>();
        builder.AddSingleton<ValidatedServiceConsumer>();

        // Act
        var exception = Should.Throw<AggregateException>(() => ((IServiceProviderBuilder)builder).Build());

        // Assert
        exception.InnerExceptions.ShouldContain(inner =>
            inner.Message.Contains($"Cannot consume scoped service '{typeof(IValidatedService)}' from singleton '{typeof(ValidatedServiceConsumer)}'"));
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Build: Should reject a singleton reaching a scoped service through an already validated transient")]
    public void Build_WhenSingletonReachesScopedServiceThroughValidatedTransient_ShouldThrow()
    {
        // Arrange: the transient is validated, and memoized, before the singleton that consumes it.
        using var builder = CreateBuilder(validateOnBuild: true);
        builder.AddScoped<ScopedDependency>();
        builder.AddTransient<TransientOverScoped>();
        builder.AddSingleton<SingletonOverTransient>();

        // Act
        var exception = Should.Throw<AggregateException>(() => ((IServiceProviderBuilder)builder).Build());

        // Assert
        exception.InnerExceptions.Count.ShouldBe(1);
        exception.InnerExceptions[0].Message.ShouldContain(
            $"Cannot consume scoped service '{typeof(ScopedDependency)}' from singleton '{typeof(SingletonOverTransient)}'");
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should reject a singleton reaching a scoped service through an already resolved transient")]
    public void GetService_WhenSingletonReachesScopedServiceThroughResolvedTransient_ShouldThrow()
    {
        // Arrange
        using var provider = Build(validateOnBuild: false, builder =>
        {
            builder.AddScoped<ScopedDependency>();
            builder.AddTransient<TransientOverScoped>();
            builder.AddSingleton<SingletonOverTransient>();
        });
        using (IServiceScope scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TransientOverScoped>();
        }

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => provider.GetService(typeof(SingletonOverTransient)));

        // Assert
        exception.Message.ShouldContain($"Cannot consume scoped service '{typeof(ScopedDependency)}'");
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should reject a transient that needs a scoped service from root")]
    public void GetService_WhenTransientNeedsScopedServiceFromRoot_ShouldThrow()
    {
        // Arrange
        using var provider = Build(validateOnBuild: false, builder =>
        {
            builder.AddScoped<ScopedDependency>();
            builder.AddTransient<TransientOverScoped>();
        });

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => provider.GetService(typeof(TransientOverScoped)));

        // Assert
        exception.Message.ShouldContain($"because it requires scoped service '{typeof(ScopedDependency)}'");
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Build: Should validate a deep graph of shared dependencies once per service")]
    public void Build_WithDeepSharedDependencyGraph_ShouldValidateEachServiceOnce()
    {
        // Arrange: 66 services and 2^32 paths. Walking every path took seconds by 26 layers and
        // roughly quadrupled with every two more.
        using var builder = CreateBuilder(validateOnBuild: true);
        DiamondGraph.AddTransients(builder);

        // Act
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                ((IServiceProviderBuilder)builder).Build();
            }
            catch (Exception exception)
            {
                // The assertion below reports whatever validation threw.
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        worker.Start();
        bool finished = worker.Join(TimeSpan.FromSeconds(30));

        // Assert
        finished.ShouldBeTrue("Validation walked every path through the graph instead of every service.");
        failure.ShouldBeNull();
    }

    private static ServiceProviderBuilder CreateBuilder(bool validateOnBuild) =>
        new ServiceProviderBuilder(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = validateOnBuild,
        });

    private static ServiceProvider Build(bool validateOnBuild, Action<ServiceProviderBuilder> configure)
    {
        using var builder = CreateBuilder(validateOnBuild);
        configure(builder);
        return (ServiceProvider)((IServiceProviderBuilder)builder).Build();
    }
}
