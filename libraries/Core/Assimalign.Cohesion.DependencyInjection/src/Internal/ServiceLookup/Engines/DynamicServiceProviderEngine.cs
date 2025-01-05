using System;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class DynamicServiceProviderEngine : CompiledServiceProviderEngine
{
    private readonly ServiceProvider serviceProvider;

    public DynamicServiceProviderEngine(ServiceProvider serviceProvider) : base(serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public override Func<ServiceProviderEngineScope, object> RealizeService(CallSiteService callSite)
    {
        int callCount = 0;
        return scope =>
        {
            // Resolve the result before we increment the call count, this ensures that singletons
            // won't cause any side effects during the compilation of the resolve function.
            var result = CallSiteRuntimeResolverVisitor.Instance.Resolve(callSite, scope);

            if (Interlocked.Increment(ref callCount) == 2)
            {
                // Don't capture the ExecutionContext when forking to build the compiled version of the
                // resolve function
                _ = ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try
                    {
                        serviceProvider.ReplaceServiceAccessor(callSite, base.RealizeService(callSite));
                    }
                    catch (Exception ex)
                    {
                        ServiceEventSource.Log.ServiceRealizationFailed(ex, serviceProvider.GetHashCode());

                        Debug.Fail($"We should never get exceptions from the background compilation.{Environment.NewLine}{ex}");
                    }
                },
                null);
            }

            return result;
        };
    }
}
