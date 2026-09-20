using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection.Internal;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies per-provider resolver compilation policy and interpreted service lifetimes.
/// </summary>
public sealed class ServiceProviderDynamicCodeTests
{
    /// <summary>
    /// Verifies that existing callers retain the runtime-dependent default resolver.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dynamic code: Defaults preserve runtime-dependent resolver selection")]
    public void Build_WithDefaultOptions_ShouldPreserveResolverSelection()
    {
        // Arrange
        var options = new ServiceProviderOptions();
        using var builder = new ServiceProviderBuilder(options);

        // Act
        using var provider = (ServiceProvider)((IServiceProviderBuilder)builder).Build();

        // Assert
        options.EnableDynamicCode.ShouldBeTrue();
        if (RuntimeFeature.IsDynamicCodeCompiled)
        {
            (provider.engine is DynamicServiceProviderEngine).ShouldBeTrue();
        }
        else
        {
            provider.engine.ShouldBeSameAs(RuntimeServiceProviderEngine.Instance);
        }
    }

    /// <summary>
    /// Verifies that disabling compilation preserves factory lifetimes and disposal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dynamic code: Disabled resolver preserves lifetimes and disposal")]
    public void Build_WithDynamicCodeDisabled_ShouldInterpretRepeatedFactoryResolution()
    {
        // Arrange
        var options = new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateScopes = true,
            ValidateOnBuild = true,
        };
        using var builder = new ServiceProviderBuilder(options);
        var borrowed = new BorrowedService();
        builder.AddSingleton(borrowed);
        builder.AddSingleton(_ => new SingletonService());
        builder.AddScoped(_ => new ScopedService());
        builder.AddTransient(services => new TransientService(
            services.GetRequiredService<SingletonService>(),
            services.GetRequiredService<ScopedService>()));
        using var provider = (ServiceProvider)((IServiceProviderBuilder)builder).Build();

        // Act
        // The policy belongs to the built provider, even if the caller later edits its options.
        options.EnableDynamicCode = true;
        SingletonService singleton;
        ScopedService scoped;
        using (var scope = provider.CreateScope())
        {
            singleton = scope.ServiceProvider.GetRequiredService<SingletonService>();
            scoped = scope.ServiceProvider.GetRequiredService<ScopedService>();
            TransientService? previous = null;
            for (int index = 0; index < 128; index++)
            {
                var current = scope.ServiceProvider.GetRequiredService<TransientService>();
                current.ShouldNotBeSameAs(previous);
                current.Singleton.ShouldBeSameAs(singleton);
                current.Scoped.ShouldBeSameAs(scoped);
                scope.ServiceProvider.GetServices<TransientService>().Single().Scoped.ShouldBeSameAs(scoped);
                previous = current;
            }
            scoped.DisposeCount.ShouldBe(0);
        }

        // Assert
        provider.engine.ShouldBeSameAs(RuntimeServiceProviderEngine.Instance);
        scoped.DisposeCount.ShouldBe(1);
        using (var secondScope = provider.CreateScope())
        {
            secondScope.ServiceProvider.GetRequiredService<ScopedService>().ShouldNotBeSameAs(scoped);
            secondScope.ServiceProvider.GetRequiredService<SingletonService>().ShouldBeSameAs(singleton);
        }
        provider.GetRequiredService<BorrowedService>().ShouldBeSameAs(borrowed);
        provider.Dispose();
        singleton.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
    }

    /// <summary>
    /// Verifies that interpreted resolution still rejects scoped services from the root provider.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dynamic code: Disabled resolver preserves scope validation")]
    public void Resolve_WithDynamicCodeDisabled_ShouldValidateScopes()
    {
        // Arrange
        using var builder = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        builder.AddScoped(_ => new ScopedService());
        using var provider = (ServiceProvider)((IServiceProviderBuilder)builder).Build();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<ScopedService>());
        provider.engine.ShouldBeSameAs(RuntimeServiceProviderEngine.Instance);
    }

    /// <summary>
    /// Verifies on JIT that real compilation diagnostics occur only for the enabled control provider.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - Dynamic code: Disabled JIT provider emits no resolver IL or expressions")]
    public async Task Resolve_OnJitWithDynamicCodeDisabled_ShouldNeverEmitResolverCode()
    {
        // NativeAOT's runtime selection is covered separately; this test needs an emitting control.
        if (!RuntimeFeature.IsDynamicCodeCompiled)
        {
            return;
        }

        // Arrange
        using var enabledBuilder = new ServiceProviderBuilder();
        using var disabledBuilder = new ServiceProviderBuilder(new ServiceProviderOptions { EnableDynamicCode = false });
        enabledBuilder.AddTransient(_ => new BorrowedService());
        disabledBuilder.AddTransient(_ => new BorrowedService());
        using var enabled = (ServiceProvider)((IServiceProviderBuilder)enabledBuilder).Build();
        using var disabled = (ServiceProvider)((IServiceProviderBuilder)disabledBuilder).Build();
        using var listener = new CompilationListener(enabled, disabled);

        // Act
        for (int index = 0; index < 128; index++)
        {
            disabled.GetRequiredService<BorrowedService>().ShouldNotBeNull();
            enabled.GetRequiredService<BorrowedService>().ShouldNotBeNull();
        }
        await listener.EnabledCompilation.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        // The positive control proves diagnostics are active and the repeated-resolution threshold
        // was crossed. The runtime-engine identity excludes any delayed compilation work as well.
        listener.EnabledCompilationCount.ShouldBeGreaterThan(0);
        listener.DisabledCompilationCount.ShouldBe(0);
        disabled.engine.ShouldBeSameAs(RuntimeServiceProviderEngine.Instance);
    }

    private sealed class CompilationListener : EventListener
    {
        private readonly int _enabledProviderId;
        private readonly int _disabledProviderId;
        private readonly TaskCompletionSource<bool> _enabledCompilation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enabledCompilationCount;
        private int _disabledCompilationCount;

        internal CompilationListener(ServiceProvider enabled, ServiceProvider disabled)
        {
            _enabledProviderId = enabled.GetHashCode();
            _disabledProviderId = disabled.GetHashCode();
            EnableEvents(ServiceEventSource.Log, EventLevel.Verbose, EventKeywords.All);
        }

        internal Task EnabledCompilation => _enabledCompilation.Task;
        internal int EnabledCompilationCount => Volatile.Read(ref _enabledCompilationCount);
        internal int DisabledCompilationCount => Volatile.Read(ref _disabledCompilationCount);

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId is not (3 or 4) || eventData.Payload is not { Count: >= 3 } payload)
            {
                return;
            }

            if (payload[2] is int providerId)
            {
                if (providerId == _enabledProviderId)
                {
                    Interlocked.Increment(ref _enabledCompilationCount);
                    _enabledCompilation.TrySetResult(true);
                }
                if (providerId == _disabledProviderId)
                {
                    Interlocked.Increment(ref _disabledCompilationCount);
                }
            }
        }
    }

    private sealed class BorrowedService : IDisposable
    {
        internal int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class SingletonService : IDisposable
    {
        internal int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class ScopedService : IDisposable
    {
        internal int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed record TransientService(SingletonService Singleton, ScopedService Scoped);
}
