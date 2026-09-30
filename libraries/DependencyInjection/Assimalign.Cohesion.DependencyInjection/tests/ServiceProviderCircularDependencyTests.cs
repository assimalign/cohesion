using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using static Assimalign.Cohesion.DependencyInjection.Tests.CircularDependencyFixtures;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies that a singleton resolved again while it is being created, which only a factory can
/// cause, throws instead of deadlocking.
/// </summary>
public sealed class ServiceProviderCircularDependencyTests
{
    private static readonly TimeSpan _deadlockTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should throw when a singleton factory resolves its own service")]
    public void GetService_WhenSingletonFactoryResolvesItself_ShouldThrowInsteadOfDeadlocking()
    {
        // Arrange
        using var provider = Build(builder =>
            builder.AddSingleton<SelfReferencingService>(services =>
                new SelfReferencingService(services.GetRequiredService<SelfReferencingService>())));

        // Act
        Exception? failure = ResolveOnWorkerThread(provider, typeof(SelfReferencingService));

        // Assert
        var exception = failure.ShouldBeOfType<InvalidOperationException>();
        exception.Message.ShouldContain("circular dependency");
        exception.Message.ShouldContain(nameof(SelfReferencingService));
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should throw when singleton factories resolve each other")]
    public void GetService_WhenSingletonFactoriesResolveEachOther_ShouldThrowInsteadOfDeadlocking()
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddSingleton<FactoryCycleA>(services => new FactoryCycleA(services.GetRequiredService<FactoryCycleB>()));
            builder.AddSingleton<FactoryCycleB>(services => new FactoryCycleB(services.GetRequiredService<FactoryCycleA>()));
        });

        // Act
        Exception? failure = ResolveOnWorkerThread(provider, typeof(FactoryCycleA));

        // Assert
        failure.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("circular dependency");
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should throw when a factory resolves the service that depends on it")]
    public void GetService_WhenFactoryResolvesItsConstructedDependent_ShouldThrowInsteadOfDeadlocking()
    {
        // Arrange: the constructor dependency is visible to call-site construction, the factory's is not.
        using var provider = Build(builder =>
        {
            builder.AddSingleton<ConstructedCycleService>();
            builder.AddSingleton<ClosingFactoryService>(services =>
            {
                services.GetRequiredService<ConstructedCycleService>();
                return new ClosingFactoryService();
            });
        });

        // Act
        Exception? failure = ResolveOnWorkerThread(provider, typeof(ConstructedCycleService));

        // Assert
        var exception = failure.ShouldBeOfType<InvalidOperationException>();
        exception.Message.ShouldContain("circular dependency");
        exception.Message.ShouldContain(nameof(ConstructedCycleService));
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should resolve after a failed circular resolution once the cycle is gone")]
    public void GetService_AfterCircularResolutionFailed_ShouldResolveWhenTheCycleIsGone()
    {
        // Arrange
        bool resolveItself = true;
        using var provider = Build(builder =>
            builder.AddSingleton<SelfReferencingService>(services => resolveItself
                ? new SelfReferencingService(services.GetRequiredService<SelfReferencingService>())
                : new SelfReferencingService(null)));
        ResolveOnWorkerThread(provider, typeof(SelfReferencingService)).ShouldBeOfType<InvalidOperationException>();
        resolveItself = false;

        // Act
        var service = provider.GetRequiredService<SelfReferencingService>();

        // Assert
        service.Inner.ShouldBeNull();
        provider.GetRequiredService<SelfReferencingService>().ShouldBeSameAs(service);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should reject a same-thread re-entry without creating a second singleton")]
    public async Task GetService_WhenSingletonConstructionResolvesItselfOnTheSameThread_ShouldCreateOneInstance()
    {
        // Arrange
        using var provider = Build(builder =>
        {
            builder.AddSingleton<ReentrantSingleton>();
            builder.AddSingleton<ReentrantDependency>();
            builder.AddSingleton<ReentrantSingletonConsumer>();
        });

        // Act
        var consumer = provider.GetRequiredService<ReentrantSingletonConsumer>();
        await consumer.Singleton.Initialization.WaitAsync(_deadlockTimeout);
        var singleton = provider.GetRequiredService<ReentrantSingleton>();
        var dependency = provider.GetRequiredService<ReentrantDependency>();

        // Assert
        singleton.ShouldBeSameAs(consumer.Singleton);
        dependency.SingletonInstanceCount.ShouldBe(1);
        dependency.ResolutionException.ShouldNotBeNull().Message.ShouldContain("circular dependency");
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should make another thread wait for a singleton under construction")]
    public async Task GetService_WhenAnotherThreadResolvesSingletonUnderConstruction_ShouldReturnTheSameInstance()
    {
        // Arrange
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var provider = Build(builder =>
            builder.AddSingleton<SlowService>(_ =>
            {
                entered.Set();
                release.Wait(_deadlockTimeout);
                return new SlowService();
            }));
        Task<object> first = Task.Run(() => provider.GetService(typeof(SlowService)));
        entered.Wait(_deadlockTimeout).ShouldBeTrue();

        // Act: the second resolution blocks on the singleton's lock rather than failing.
        Task<object> second = Task.Run(() => provider.GetService(typeof(SlowService)));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        bool secondFinishedEarly = second.IsCompleted;
        release.Set();
        object[] results = await Task.WhenAll(first, second).WaitAsync(_deadlockTimeout);

        // Assert
        secondFinishedEarly.ShouldBeFalse();
        results[1].ShouldBeSameAs(results[0]);
    }

    private static ServiceProvider Build(Action<ServiceProviderBuilder> configure)
    {
        using var builder = new ServiceProviderBuilder();
        configure(builder);
        return (ServiceProvider)((IServiceProviderBuilder)builder).Build();
    }

    // Before the fix this deadlocked rather than throwing, so it runs on a background thread that the
    // test abandons if it never finishes.
    private static Exception? ResolveOnWorkerThread(IServiceProvider provider, Type serviceType)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                provider.GetService(serviceType);
            }
            catch (Exception exception)
            {
                // The test asserts on whatever resolution threw.
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();
        worker.Join(_deadlockTimeout).ShouldBeTrue("Resolution never finished: the circular dependency deadlocked.");
        return failure;
    }
}
