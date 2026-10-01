using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection.Internal;

using static Assimalign.Cohesion.DependencyInjection.Tests.CircularDependencyFixtures;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies that a singleton or scoped service resolved again while it is being created, which only a
/// factory can cause, throws instead of deadlocking.
/// </summary>
public sealed class ServiceProviderCircularDependencyTests
{
    private static readonly TimeSpan _deadlockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The resolver that creates scoped services. The compiled resolvers are forced on so the first
    /// resolution already runs compiled code, rather than after the dynamic engine's warm-up.
    /// </summary>
    public enum ScopedResolver
    {
        Interpreted,
        Emitted,
        Expressions,
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should throw when a scoped factory resolves its own service in a scope")]
    [InlineData(ScopedResolver.Interpreted)]
    [InlineData(ScopedResolver.Emitted)]
    [InlineData(ScopedResolver.Expressions)]
    public void GetService_WhenScopedFactoryResolvesItselfInScope_ShouldThrowInsteadOfDeadlocking(ScopedResolver resolver)
    {
        if (!CanRun(resolver))
        {
            return;
        }

        // Arrange
        using var provider = Build(resolver, builder =>
            builder.AddScoped<SelfReferencingService>(services =>
                new SelfReferencingService(services.GetRequiredService<SelfReferencingService>())));
        IServiceScope scope = provider.CreateScope(); // Not a using: see ResolveOnWorkerThread.

        // Act
        Exception? failure = ResolveOnWorkerThread(scope.ServiceProvider, typeof(SelfReferencingService));

        // Assert
        var exception = failure.ShouldBeOfType<InvalidOperationException>();
        exception.Message.ShouldContain("circular dependency");
        exception.Message.ShouldContain(nameof(SelfReferencingService));
        scope.Dispose();
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should throw when scoped factories resolve each other in a scope")]
    [InlineData(ScopedResolver.Interpreted)]
    [InlineData(ScopedResolver.Emitted)]
    [InlineData(ScopedResolver.Expressions)]
    public void GetService_WhenScopedFactoriesResolveEachOtherInScope_ShouldThrowInsteadOfDeadlocking(ScopedResolver resolver)
    {
        if (!CanRun(resolver))
        {
            return;
        }

        // Arrange
        using var provider = Build(resolver, builder =>
        {
            builder.AddScoped<FactoryCycleA>(services => new FactoryCycleA(services.GetRequiredService<FactoryCycleB>()));
            builder.AddScoped<FactoryCycleB>(services => new FactoryCycleB(services.GetRequiredService<FactoryCycleA>()));
        });
        IServiceScope scope = provider.CreateScope(); // Not a using: see ResolveOnWorkerThread.

        // Act
        Exception? failure = ResolveOnWorkerThread(scope.ServiceProvider, typeof(FactoryCycleA));

        // Assert
        failure.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("circular dependency");
        scope.Dispose();
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should resolve a scoped service in the same scope once its cycle is gone")]
    [InlineData(ScopedResolver.Interpreted)]
    [InlineData(ScopedResolver.Emitted)]
    [InlineData(ScopedResolver.Expressions)]
    public void GetService_AfterScopedCircularResolutionFailed_ShouldResolveInTheSameScope(ScopedResolver resolver)
    {
        if (!CanRun(resolver))
        {
            return;
        }

        // Arrange: a failed creation must give its reservation back, or the scope would report every
        // later request as a cycle.
        bool resolveItself = true;
        using var provider = Build(resolver, builder =>
            builder.AddScoped<SelfReferencingService>(services => resolveItself
                ? new SelfReferencingService(services.GetRequiredService<SelfReferencingService>())
                : new SelfReferencingService(null)));
        IServiceScope scope = provider.CreateScope(); // Not a using: see ResolveOnWorkerThread.
        ResolveOnWorkerThread(scope.ServiceProvider, typeof(SelfReferencingService)).ShouldBeOfType<InvalidOperationException>();
        resolveItself = false;

        // Act
        var service = scope.ServiceProvider.GetRequiredService<SelfReferencingService>();

        // Assert
        service.Inner.ShouldBeNull();
        scope.ServiceProvider.GetRequiredService<SelfReferencingService>().ShouldBeSameAs(service);
        scope.Dispose();
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should create a scoped service again after its factory threw")]
    [InlineData(ScopedResolver.Interpreted)]
    [InlineData(ScopedResolver.Emitted)]
    [InlineData(ScopedResolver.Expressions)]
    public void GetService_AfterScopedFactoryThrew_ShouldCreateTheServiceOnTheNextRequest(ScopedResolver resolver)
    {
        if (!CanRun(resolver))
        {
            return;
        }

        // Arrange
        int attempts = 0;
        using var provider = Build(resolver, builder =>
            builder.AddScoped<SlowService>(_ => ++attempts == 1
                ? throw new InvalidOperationException("First attempt fails.")
                : new SlowService()));
        using IServiceScope scope = provider.CreateScope();
        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetService(typeof(SlowService)))
            .Message.ShouldBe("First attempt fails.");

        // Act
        var service = scope.ServiceProvider.GetRequiredService<SlowService>();

        // Assert
        scope.ServiceProvider.GetRequiredService<SlowService>().ShouldBeSameAs(service);
        attempts.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should share a scoped service that another scoped factory resolves")]
    [InlineData(ScopedResolver.Interpreted)]
    [InlineData(ScopedResolver.Emitted)]
    [InlineData(ScopedResolver.Expressions)]
    public void GetService_WhenScopedFactoryResolvesAnotherScopedService_ShouldShareItWithTheScope(ScopedResolver resolver)
    {
        if (!CanRun(resolver))
        {
            return;
        }

        // Arrange
        using var provider = Build(resolver, builder =>
        {
            builder.AddScoped<ScopedLeaf>();
            builder.AddScoped<NestedScopedService>(services => new NestedScopedService(services.GetRequiredService<ScopedLeaf>()));
        });
        using IServiceScope scope = provider.CreateScope();

        // Act
        var nested = scope.ServiceProvider.GetRequiredService<NestedScopedService>();

        // Assert
        scope.ServiceProvider.GetRequiredService<ScopedLeaf>().ShouldBeSameAs(nested.Leaf);
        scope.ServiceProvider.GetRequiredService<NestedScopedService>().ShouldBeSameAs(nested);
    }

    [Theory(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should let two scopes create one scoped service at the same time")]
    [InlineData(ScopedResolver.Interpreted)]
    [InlineData(ScopedResolver.Emitted)]
    [InlineData(ScopedResolver.Expressions)]
    public async Task GetService_WhenTwoScopesCreateTheSameServiceAtOnce_ShouldNotReportACycle(ScopedResolver resolver)
    {
        if (!CanRun(resolver))
        {
            return;
        }

        // Arrange: each factory waits inside its creation until the other has started, so both scopes
        // hold a reservation for the same service at the same time.
        using var bothCreating = new Barrier(2);
        using var provider = Build(resolver, builder =>
            builder.AddScoped<SlowService>(_ =>
            {
                bothCreating.SignalAndWait(_deadlockTimeout).ShouldBeTrue("The other scope never started creating the service.");
                return new SlowService();
            }));

        // Act
        Task<object> first = Task.Run(() => ResolveInNewScope(provider));
        Task<object> second = Task.Run(() => ResolveInNewScope(provider));
        object[] services = await Task.WhenAll(first, second).WaitAsync(_deadlockTimeout);

        // Assert
        services[0].ShouldBeOfType<SlowService>();
        services[1].ShouldBeOfType<SlowService>().ShouldNotBeSameAs(services[0]);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - GetService: Should throw when a scoped factory resolves its own service from the root provider")]
    public void GetService_WhenScopedFactoryResolvesItselfFromRoot_ShouldThrowInsteadOfDeadlocking()
    {
        // Arrange: resolved from the root provider, a scoped service is cached like a singleton.
        using var provider = Build(builder =>
            builder.AddScoped<SelfReferencingService>(services =>
                new SelfReferencingService(services.GetRequiredService<SelfReferencingService>())));

        // Act
        Exception? failure = ResolveOnWorkerThread(provider, typeof(SelfReferencingService));

        // Assert
        failure.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("circular dependency");
    }

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

    private static ServiceProvider Build(ScopedResolver resolver, Action<ServiceProviderBuilder> configure)
    {
        using var builder = new ServiceProviderBuilder(new ServiceProviderOptions { EnableDynamicCode = false });
        configure(builder);
        var provider = (ServiceProvider)((IServiceProviderBuilder)builder).Build();

        // Without dynamic code the provider interprets; the compiled engines build a resolver on first use.
        provider.engine = resolver switch
        {
            ScopedResolver.Emitted => new ILEmitServiceProviderEngine(provider),
            ScopedResolver.Expressions => new ExpressionsServiceProviderEngine(provider),
            _ => provider.engine,
        };
        return provider;
    }

    // The compiled resolvers generate code, which NativeAOT cannot run.
    private static bool CanRun(ScopedResolver resolver) =>
        resolver == ScopedResolver.Interpreted || RuntimeFeature.IsDynamicCodeCompiled;

    private static object ResolveInNewScope(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SlowService>();
    }

    // Before the fix this deadlocked rather than throwing, so it runs on a background thread that the
    // test abandons if it never finishes. An abandoned resolution keeps holding its scope's lock, which
    // disposing the scope takes, so a test that passes a scope disposes it only after this returns.
    // Disposing it from a using would hang the test in cleanup instead of failing it.
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
