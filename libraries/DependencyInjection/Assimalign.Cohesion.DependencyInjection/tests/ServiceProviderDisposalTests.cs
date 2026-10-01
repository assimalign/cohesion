using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using static Assimalign.Cohesion.DependencyInjection.Tests.DisposalFixtures;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies that a scope disposes every captured service exactly once, even when one of them throws.
/// </summary>
public sealed class ServiceProviderDisposalTests
{
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should dispose every service when one throws, then rethrow its exception")]
    public void Dispose_WhenOneServiceThrows_ShouldDisposeTheOthersAndRethrowIt()
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddTransient<TrackedDisposable>();
            builder.AddTransient<ThrowingDisposable>();
        });
        IServiceScope scope = provider.CreateScope();
        var first = scope.ServiceProvider.GetRequiredService<TrackedDisposable>();
        var thrower = scope.ServiceProvider.GetRequiredService<ThrowingDisposable>();
        var last = scope.ServiceProvider.GetRequiredService<TrackedDisposable>();

        // Act
        var exception = Should.Throw<InvalidOperationException>(scope.Dispose);

        // Assert
        exception.Message.ShouldBe(DisposeFailure);
        thrower.IsDisposed.ShouldBeTrue();
        first.DisposeCount.ShouldBe(1);
        last.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should aggregate the exceptions of several throwing services")]
    public void Dispose_WhenSeveralServicesThrow_ShouldThrowAggregateException()
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddTransient<TrackedDisposable>();
            builder.AddTransient<ThrowingDisposable>();
        });
        IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ThrowingDisposable>();
        var tracked = scope.ServiceProvider.GetRequiredService<TrackedDisposable>();
        scope.ServiceProvider.GetRequiredService<ThrowingDisposable>();

        // Act
        var exception = Should.Throw<AggregateException>(scope.Dispose);

        // Assert
        exception.InnerExceptions.Count.ShouldBe(2);
        exception.InnerExceptions.ShouldAllBe(inner => inner is InvalidOperationException && inner.Message == DisposeFailure);
        tracked.DisposeCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - DisposeAsync: Should dispose every service when one throws, then report its exception")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WhenOneServiceThrows_ShouldDisposeTheOthersAndReportIt(bool throwAfterYielding)
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddTransient<TrackedDisposable>();
            builder.AddTransient<ThrowingDisposable>();
            builder.AddTransient<YieldingThrowingDisposable>();
        });
        AsyncServiceScope scope = provider.CreateAsyncScope();
        var first = scope.ServiceProvider.GetRequiredService<TrackedDisposable>();
        if (throwAfterYielding)
        {
            scope.ServiceProvider.GetRequiredService<YieldingThrowingDisposable>();
        }
        else
        {
            scope.ServiceProvider.GetRequiredService<ThrowingDisposable>();
        }
        var last = scope.ServiceProvider.GetRequiredService<TrackedDisposable>();

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () => await scope.DisposeAsync());

        // Assert
        exception.Message.ShouldBe(DisposeFailure);
        first.DisposeCount.ShouldBe(1);
        last.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - DisposeAsync: Should aggregate the exceptions of several throwing services")]
    public async Task DisposeAsync_WhenSeveralServicesThrow_ShouldThrowAggregateException()
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddTransient<TrackedDisposable>();
            builder.AddTransient<ThrowingDisposable>();
            builder.AddTransient<YieldingThrowingDisposable>();
        });
        AsyncServiceScope scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ThrowingDisposable>();
        var tracked = scope.ServiceProvider.GetRequiredService<TrackedDisposable>();
        scope.ServiceProvider.GetRequiredService<YieldingThrowingDisposable>();

        // Act
        var exception = await Should.ThrowAsync<AggregateException>(async () => await scope.DisposeAsync());

        // Assert
        exception.InnerExceptions.Count.ShouldBe(2);
        tracked.DisposeCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should dispose a singleton exposed through forwarding factories once")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_WithSingletonForwardedByFactories_ShouldDisposeItOnce(bool disposeAsync)
    {
        // Arrange
        var provider = Build(builder =>
        {
            builder.AddSingleton<SharedDisposable>();
            builder.AddSingleton<ISharedService>(services => services.GetRequiredService<SharedDisposable>());
            builder.AddSingleton<IOtherSharedService>(services => services.GetRequiredService<SharedDisposable>());
        });
        var shared = provider.GetRequiredService<SharedDisposable>();
        provider.GetRequiredService<ISharedService>().ShouldBeSameAs(shared);
        provider.GetRequiredService<IOtherSharedService>().ShouldBeSameAs(shared);

        // Act
        if (disposeAsync)
        {
            await provider.DisposeAsync();
        }
        else
        {
            provider.Dispose();
        }

        // Assert
        shared.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should dispose a scoped service exposed through a forwarding factory once")]
    public void Dispose_WithScopedServiceForwardedByFactory_ShouldDisposeItOnce()
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddScoped<SharedDisposable>();
            builder.AddScoped<ISharedService>(services => services.GetRequiredService<SharedDisposable>());
        });
        IServiceScope scope = provider.CreateScope();
        var shared = scope.ServiceProvider.GetRequiredService<SharedDisposable>();
        scope.ServiceProvider.GetRequiredService<ISharedService>().ShouldBeSameAs(shared);

        // Act
        scope.Dispose();

        // Assert
        shared.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should dispose a shared singleton after the services that depend on it")]
    public void Dispose_WithSharedSingleton_ShouldDisposeItAfterItsDependents()
    {
        // Arrange
        var provider = Build(builder =>
        {
            builder.AddSingleton<SharedDisposable>();
            builder.AddSingleton<SharedDependent>();
            builder.AddSingleton<ISharedService>(services => services.GetRequiredService<SharedDisposable>());
        });
        var dependent = provider.GetRequiredService<SharedDependent>();
        var shared = provider.GetRequiredService<SharedDisposable>();
        provider.GetRequiredService<ISharedService>().ShouldBeSameAs(shared);

        // Act
        provider.Dispose();

        // Assert
        dependent.SharedWasLiveAtDisposal.ShouldBeTrue();
        shared.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dispose: Should dispose a singleton exposed through many forwarding factories once")]
    public void Dispose_WithManyForwardingFactories_ShouldDisposeTheSharedSingletonOnce()
    {
        // Arrange: more captures than the linear scan handles, so the set-based path runs.
        const int aliasCount = 20;
        var provider = Build(builder =>
        {
            builder.AddSingleton<SharedDisposable>();
            for (int i = 0; i < aliasCount; i++)
            {
                builder.AddSingleton<ISharedService>(services => services.GetRequiredService<SharedDisposable>());
            }
        });
        var shared = provider.GetRequiredService<SharedDisposable>();
        provider.GetServices<ISharedService>().ShouldAllBe(alias => ReferenceEquals(alias, shared));

        // Act
        provider.Dispose();

        // Assert
        shared.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - DisposeAsync: Should skip removed duplicates after an asynchronous disposal yields")]
    public async Task DisposeAsync_WhenADisposalYields_ShouldStillDisposeTheSharedSingletonOnce()
    {
        // Arrange: the yielding service is captured last, so it is disposed first and the rest of the
        // work, removed duplicates included, runs in the asynchronous continuation.
        var provider = Build(builder =>
        {
            builder.AddSingleton<SharedDisposable>();
            builder.AddSingleton<ISharedService>(services => services.GetRequiredService<SharedDisposable>());
            builder.AddSingleton<IOtherSharedService>(services => services.GetRequiredService<SharedDisposable>());
            builder.AddSingleton<YieldingDisposable>();
        });
        var shared = provider.GetRequiredService<SharedDisposable>();
        provider.GetRequiredService<ISharedService>();
        provider.GetRequiredService<IOtherSharedService>();
        var yielding = provider.GetRequiredService<YieldingDisposable>();

        // Act
        await provider.DisposeAsync();

        // Assert
        yielding.IsDisposed.ShouldBeTrue();
        shared.DisposeCount.ShouldBe(1);
    }

    private static ServiceProvider Build(Action<ServiceProviderBuilder> configure)
    {
        using var builder = new ServiceProviderBuilder();
        configure(builder);
        return (ServiceProvider)((IServiceProviderBuilder)builder).Build();
    }
}
