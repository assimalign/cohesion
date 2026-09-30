using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.DependencyInjection;

using Assimalign.Cohesion.DependencyInjection.Internal;

/// <summary>
/// The default IServiceProvider.
/// </summary>
public sealed class ServiceProvider : IServiceProvider, IDisposable, IAsyncDisposable
{
    private readonly CallSiteValidatorVisitor? _callSiteValidator;
    private readonly Func<Type, ServiceAccessor> _createServiceAccessor;

    // Internal for testing
    internal ServiceProviderEngine engine;

    internal bool IsDisposed;
    private readonly ConcurrentDictionary<Type, ServiceAccessor> _serviceAccessors;

    internal CallSiteFactory CallSiteFactory { get; }
    internal ServiceProviderEngineScope Root { get; }
    internal static bool VerifyOpenGenericServiceTrimmability { get; } = AppContext.TryGetSwitch(
        "Assimalign.Cohesion.DependencyInjection.VerifyOpenGenericServiceTrimmability",
        out bool verifyOpenGenerics) ? verifyOpenGenerics : false;

    internal ServiceProvider(ServiceContainer container, ServiceProviderOptions options)
    {
        // note that Root needs to be set before calling GetEngine(), because the engine may need to access Root
        Root = new ServiceProviderEngineScope(this, isRootScope: true);
        engine = GetEngine(options.EnableDynamicCode);
        _createServiceAccessor = CreateServiceAccessor;
        _serviceAccessors = new ConcurrentDictionary<Type, ServiceAccessor>();

        CallSiteFactory = new CallSiteFactory(container);
        CallSiteFactory.Add(typeof(IServiceProvider), new ServiceProviderCallSite()); // The list of built in services that aren't part of the list of service descriptors. keep this in sync with CallSiteFactory.IsService
        CallSiteFactory.Add(typeof(IServiceScopeFactory), new ConstantCallSite(typeof(IServiceScopeFactory), Root));
        CallSiteFactory.Add(typeof(IServiceLookup), new ConstantCallSite(typeof(IServiceLookup), CallSiteFactory));

        if (options.ValidateScopes)
        {
            _callSiteValidator = new CallSiteValidatorVisitor();
        }
        if (options.ValidateOnBuild)
        {
            List<Exception>? exceptions = null;

            foreach (ServiceDescriptor serviceDescriptor in container)
            {
                try
                {
                    ValidateService(serviceDescriptor);
                }
                catch (Exception exception)
                {
                    exceptions ??= new List<Exception>();
                    exceptions.Add(exception);
                }
            }
            if (exceptions is not null)
            {
                throw new AggregateException("Some services are not able to be constructed", exceptions.ToArray());
            }
        }

        ServiceEventSource.Log.ServiceProviderBuilt(this);
    }

    /// <summary>
    /// Gets the service object of the specified type.
    /// </summary>
    /// <param name="serviceType">The type of the service to get.</param>
    /// <returns>The service that was produced.</returns>
    public object GetService(Type serviceType) => GetService(serviceType, Root);

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeCore();
        Root.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return Root.DisposeAsync();
    }

    private void DisposeCore()
    {
        IsDisposed = true;
        ServiceEventSource.Log.ServiceProviderDisposed(this);
    }
    private void OnCreate(CallSiteService callSite)
    {
        _callSiteValidator?.ValidateCallSite(callSite);
    }
    private void OnResolve(CallSiteService? callSite, IServiceScope scope)
    {
        if (callSite != null)
        {
            _callSiteValidator?.ValidateResolution(callSite, scope, Root);
        }
    }
    internal object GetService(Type serviceType, ServiceProviderEngineScope serviceProviderEngineScope)
    {
        if (IsDisposed)
        {
            ThrowHelper.ThrowObjectDisposedException();
        }

        ServiceAccessor serviceAccessor = _serviceAccessors.GetOrAdd(serviceType, _createServiceAccessor);

        OnResolve(serviceAccessor.CallSite, serviceProviderEngineScope);

        ServiceEventSource.Log.ServiceResolved(this, serviceType);

        var result = serviceAccessor.RealizedService.Invoke(serviceProviderEngineScope);

        System.Diagnostics.Debug.Assert(result is null || CallSiteFactory.IsService(serviceType));

        return result;
    }
    private void ValidateService(ServiceDescriptor descriptor)
    {
        if (descriptor.ServiceType.IsGenericType && !descriptor.ServiceType.IsConstructedGenericType)
        {
            return;
        }
        try
        {
            var callSite = CallSiteFactory.GetCallSite(descriptor, new CallSiteChain());

            if (callSite != null)
            {
                OnCreate(callSite);
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Error while validating the service descriptor '{descriptor}': {exception.Message}", exception);
        }
    }
    private ServiceAccessor CreateServiceAccessor(Type serviceType)
    {
        var callSite = CallSiteFactory.GetCallSite(serviceType, new CallSiteChain());
        if (callSite != null)
        {
            ServiceEventSource.Log.CallSiteBuilt(this, serviceType, callSite);
            OnCreate(callSite);

            // Optimize singleton case
            if (callSite.Cache.Location == CallSiteResultCacheLocation.Root)
            {
                object value = CallSiteRuntimeResolverVisitor.Instance.Resolve(callSite, Root);
                return new ServiceAccessor(callSite, scope => value);
            }

            return new ServiceAccessor(callSite, engine.RealizeService(callSite));
        }

        return new ServiceAccessor(null, static _ => null);
    }
    internal void ReplaceServiceAccessor(CallSiteService callSite, Func<ServiceProviderEngineScope, object> accessor)
    {
        _serviceAccessors[callSite.ServiceType] = new ServiceAccessor(callSite, accessor);
    }
    internal IServiceScope CreateScope()
    {
        if (IsDisposed)
        {
            ThrowHelper.ThrowObjectDisposedException();
        }
        return new ServiceProviderEngineScope(this, isRootScope: false);
    }
    private ServiceProviderEngine GetEngine(bool enableDynamicCode)
    {
        // Choose before constructing a compiled engine: the dynamic engine queues compilation
        // after repeated resolutions, whereas the runtime engine has no compilation path.
        return enableDynamicCode && RuntimeFeature.IsDynamicCodeCompiled
            ? CreateDynamicEngine()
            : RuntimeServiceProviderEngine.Instance;

        [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
                Justification = "CreateDynamicEngine is guarded by EnableDynamicCode and RuntimeFeature.IsDynamicCodeCompiled.")] // see also https://github.com/dotnet/linker/issues/2715
        ServiceProviderEngine CreateDynamicEngine() => new DynamicServiceProviderEngine(this);
    }

    // The resolver for one service type, kept with the call site it was built from so that
    // resolution-time scope validation can look the call site up by its cache key.
    private sealed class ServiceAccessor
    {
        public ServiceAccessor(CallSiteService? callSite, Func<ServiceProviderEngineScope, object?> realizedService)
        {
            CallSite = callSite;
            RealizedService = realizedService;
        }

        public CallSiteService? CallSite { get; }

        public Func<ServiceProviderEngineScope, object?> RealizedService { get; }
    }
}
