using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Services whose factories or constructors resolve their own service, directly or through another.
/// </summary>
internal static class CircularDependencyFixtures
{
    internal sealed class SelfReferencingService
    {
        public SelfReferencingService(SelfReferencingService? inner)
        {
            Inner = inner;
        }

        public SelfReferencingService? Inner { get; }
    }

    internal sealed class FactoryCycleA
    {
        public FactoryCycleA(FactoryCycleB b)
        {
        }
    }

    internal sealed class FactoryCycleB
    {
        public FactoryCycleB(FactoryCycleA a)
        {
        }
    }

    /// <summary>
    /// Constructor-injected with <see cref="ClosingFactoryService"/>, whose factory resolves this service.
    /// </summary>
    internal sealed class ConstructedCycleService
    {
        public ConstructedCycleService(ClosingFactoryService dependency)
        {
        }
    }

    internal sealed class ClosingFactoryService
    {
    }

    internal sealed class SlowService
    {
    }

    /// <summary>
    /// Starts work while being constructed that resolves this singleton again on the same thread.
    /// </summary>
    internal sealed class ReentrantSingleton
    {
        public ReentrantSingleton(ReentrantDependency dependency)
        {
            dependency.SingletonInstanceCount++;
            Initialization = dependency.InitializeAsync();
        }

        public Task Initialization { get; }
    }

    internal sealed class ReentrantDependency
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly IServiceProvider _serviceProvider;

        public ReentrantDependency(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public int SingletonInstanceCount { get; set; }

        public InvalidOperationException? ResolutionException { get; private set; }

        public async Task InitializeAsync()
        {
            // Completes synchronously, so the resolution below runs inside the singleton's constructor.
            await _gate.WaitAsync();

            try
            {
                _ = _serviceProvider.GetRequiredService<ReentrantSingleton>();
            }
            catch (InvalidOperationException exception)
            {
                ResolutionException = exception;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    internal sealed class ReentrantSingletonConsumer
    {
        public ReentrantSingletonConsumer(ReentrantSingleton singleton)
        {
            Singleton = singleton;
        }

        public ReentrantSingleton Singleton { get; }
    }
}
