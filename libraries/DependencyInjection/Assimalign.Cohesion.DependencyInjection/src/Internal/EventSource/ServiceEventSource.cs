using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

/// <summary>
/// The container's diagnostics: provider lifecycle, call-site construction, resolution, scope
/// disposal, and resolver compilation.
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.DependencyInjection</c>; applications forward
/// it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Only the provider-built
/// summary is <see cref="EventLevel.Informational"/>; everything else is <see cref="EventLevel.Verbose"/>
/// detail, apart from the error a failed background compilation reports.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.DependencyInjection")]
internal sealed class ServiceEventSource : EventSource
{
    public static readonly ServiceEventSource Log = new();

    public static class Keywords
    {
        public const EventKeywords ServiceProviderInitialized = (EventKeywords)0x1;
    }

    // Event source doesn't support large payloads so we chunk large payloads like formatted call site tree and descriptors
    private const int maxChunkSize = 10 * 1024;

    // Providers built and not yet disposed, so a listener that attaches later still receives their
    // summaries. A provider that is never disposed leaves a dead reference behind; those are pruned
    // when the list has doubled since the last pruning, which bounds it by twice the live providers.
    private readonly List<WeakReference<ServiceProvider>> _providers = new();
    private int? _survivingProviders;

    private ServiceEventSource()
    {
    }

    /// <summary>The providers currently tracked for late listeners, dead references included.</summary>
    internal int TrackedProviderCount
    {
        get
        {
            lock (_providers)
            {
                return _providers.Count;
            }
        }
    }

    // NOTE
    // - The 'Start' and 'Stop' suffixes on the following event names have special meaning in EventSource. They
    //   enable creating 'activities'.
    //   For more information, take a look at the following blog post:
    //   https://blogs.msdn.microsoft.com/vancem/2015/09/14/exploring-eventsource-activity-correlation-and-causation-features/
    // - A stop event's event id must be next one after its start event.
    // - Avoid renaming methods or parameters marked with EventAttribute. EventSource uses these to form the event object.

    [NonEvent]
    public void CallSiteBuilt(ServiceProvider provider, Type serviceType, CallSiteService callSite)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            string format = CallSiteJsonFormatterVisitor.Instance.Format(callSite);
            int chunkCount = format.Length / maxChunkSize + (format.Length % maxChunkSize > 0 ? 1 : 0);
            int providerHashCode = provider.GetHashCode();
            for (int i = 0; i < chunkCount; i++)
            {
                CallSiteBuilt(
                    serviceType.ToString(),
                    format.Substring(i * maxChunkSize, Math.Min(maxChunkSize, format.Length - i * maxChunkSize)), i, chunkCount,
                    providerHashCode);
            }
        }
    }

    [NonEvent]
    public void ServiceResolved(ServiceProvider provider, Type serviceType)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ServiceResolved(serviceType.ToString(), provider.GetHashCode());
        }
    }

    [NonEvent]
    public void ExpressionTreeGenerated(ServiceProvider provider, Type serviceType, int nodeCount)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ExpressionTreeGenerated(serviceType.ToString(), nodeCount, provider.GetHashCode());
        }
    }

    [NonEvent]
    public void DynamicMethodBuilt(ServiceProvider provider, Type serviceType, int methodSize)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            DynamicMethodBuilt(serviceType.ToString(), methodSize, provider.GetHashCode());
        }
    }

    [NonEvent]
    public void ScopeDisposed(ServiceProvider provider, int scopedServicesResolved, int disposableServices)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ScopeDisposed(provider.GetHashCode(), scopedServicesResolved, disposableServices);
        }
    }

    [NonEvent]
    public void ServiceRealizationFailed(ServiceProvider provider, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ServiceRealizationFailed(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, provider.GetHashCode());
        }
    }

    [NonEvent]
    public void ServiceProviderBuilt(ServiceProvider provider)
    {
        lock (_providers)
        {
            int providers = _providers.Count;
            if (providers > 0 &&
                (_survivingProviders is int surviving
                    ? (uint)providers >= 2 * (uint)surviving
                    : providers == _providers.Capacity))
            {
                _providers.RemoveAll(static reference => !reference.TryGetTarget(out _));
                _survivingProviders = _providers.Count;
            }

            _providers.Add(new WeakReference<ServiceProvider>(provider));
        }

        WriteServiceProviderBuilt(provider);
    }

    [NonEvent]
    public void ServiceProviderDisposed(ServiceProvider provider)
    {
        lock (_providers)
        {
            for (int i = _providers.Count - 1; i >= 0; i--)
            {
                // remove the provider, along with any stale references found before it
                WeakReference<ServiceProvider> reference = _providers[i];
                if (!reference.TryGetTarget(out ServiceProvider? target) || target == provider)
                {
                    _providers.RemoveAt(i);

                    if (target is not null)
                    {
                        break;
                    }
                }
            }
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Call site built for {0} in provider {4}, chunk {2} of {3}: {1}")]
    private void CallSiteBuilt(string serviceType, string callSite, int chunkIndex, int chunkCount, int serviceProviderHashCode)
        => WriteEvent(1, serviceType, callSite, chunkIndex, chunkCount, serviceProviderHashCode);

    [Event(2, Level = EventLevel.Verbose, Message = "Resolved {0} from provider {1}")]
    private void ServiceResolved(string serviceType, int serviceProviderHashCode)
        => WriteEvent(2, serviceType, serviceProviderHashCode);

    // Written only by the expression-tree resolver, which is compiled but not selected while the IL
    // resolver is built in. Kept, like event 4, as the observable proof that a provider generated
    // code: the dynamic-code tests assert that neither event fires when EnableDynamicCode is false.
    [Event(3, Level = EventLevel.Verbose, Message = "Compiled an expression-tree resolver for {0} in provider {2}: {1} nodes")]
    private void ExpressionTreeGenerated(string serviceType, int nodeCount, int serviceProviderHashCode)
        => WriteEvent(3, serviceType, nodeCount, serviceProviderHashCode);

    // Never written under NativeAOT, which has no runtime code generation.
    [Event(4, Level = EventLevel.Verbose, Message = "Emitted an IL resolver for {0} in provider {2}: {1} bytes")]
    private void DynamicMethodBuilt(string serviceType, int methodSize, int serviceProviderHashCode)
        => WriteEvent(4, serviceType, methodSize, serviceProviderHashCode);

    [Event(5, Level = EventLevel.Verbose, Message = "Scope of provider {0} disposed after resolving {1} scoped services and capturing {2} disposable ones")]
    private void ScopeDisposed(int serviceProviderHashCode, int scopedServicesResolved, int disposableServices)
        => WriteEvent(5, serviceProviderHashCode, scopedServicesResolved, disposableServices);

    [Event(6, Level = EventLevel.Error, Message = "Compiling a resolver in provider {2} failed: {0}: {1}")]
    private void ServiceRealizationFailed(string exceptionType, string exceptionMessage, int serviceProviderHashCode)
        => WriteEvent(6, exceptionType, exceptionMessage, serviceProviderHashCode);

    [Event(7, Level = EventLevel.Informational, Keywords = Keywords.ServiceProviderInitialized,
        Message = "Provider {0} built: {1} singleton, {2} scoped and {3} transient registrations, {4} closed and {5} open generic")]
    private void ServiceProviderBuilt(int serviceProviderHashCode, int singletonServices, int scopedServices, int transientServices, int closedGenericsServices, int openGenericsServices)
        => WriteEvent(7, serviceProviderHashCode, singletonServices, scopedServices, transientServices, closedGenericsServices, openGenericsServices);

    [Event(8, Level = EventLevel.Verbose, Keywords = Keywords.ServiceProviderInitialized,
        Message = "Provider {0} descriptors, chunk {2} of {3}: {1}")]
    private void ServiceProviderDescriptors(int serviceProviderHashCode, string descriptors, int chunkIndex, int chunkCount)
        => WriteEvent(8, serviceProviderHashCode, descriptors, chunkIndex, chunkCount);

    [NonEvent]
    private void WriteServiceProviderBuilt(ServiceProvider provider)
    {
        if (!IsEnabled(EventLevel.Informational, Keywords.ServiceProviderInitialized))
        {
            return;
        }

        int singletonServices = 0;
        int scopedServices = 0;
        int transientServices = 0;
        int closedGenericsServices = 0;
        int openGenericsServices = 0;

        // The descriptor dump is Verbose detail, so the summary alone does not pay for building it.
        StringBuilder? descriptorBuilder = IsEnabled(EventLevel.Verbose, Keywords.ServiceProviderInitialized)
            ? new StringBuilder("{ \"descriptors\":[ ")
            : null;
        bool firstDescriptor = true;
        foreach (ServiceDescriptor descriptor in provider.CallSiteFactory.Descriptors)
        {
            if (descriptorBuilder is not null)
            {
                if (!firstDescriptor)
                {
                    descriptorBuilder.Append(", ");
                }

                firstDescriptor = false;
                AppendServiceDescriptor(descriptorBuilder, descriptor);
            }

            switch (descriptor.Lifetime)
            {
                case ServiceLifetime.Singleton:
                    singletonServices++;
                    break;
                case ServiceLifetime.Scoped:
                    scopedServices++;
                    break;
                case ServiceLifetime.Transient:
                    transientServices++;
                    break;
            }

            if (descriptor.ServiceType.IsGenericType)
            {
                if (descriptor.ServiceType.IsConstructedGenericType)
                {
                    closedGenericsServices++;
                }
                else
                {
                    openGenericsServices++;
                }
            }
        }

        int providerHashCode = provider.GetHashCode();
        ServiceProviderBuilt(providerHashCode, singletonServices, scopedServices, transientServices, closedGenericsServices, openGenericsServices);

        if (descriptorBuilder is null)
        {
            return;
        }

        descriptorBuilder.Append(" ] }");
        string descriptorString = descriptorBuilder.ToString();
        int chunkCount = descriptorString.Length / maxChunkSize + (descriptorString.Length % maxChunkSize > 0 ? 1 : 0);

        for (int i = 0; i < chunkCount; i++)
        {
            ServiceProviderDescriptors(
                providerHashCode,
                descriptorString.Substring(i * maxChunkSize, Math.Min(maxChunkSize, descriptorString.Length - i * maxChunkSize)), i, chunkCount);
        }
    }

    [NonEvent]
    private static void AppendServiceDescriptor(StringBuilder builder, ServiceDescriptor descriptor)
    {
        builder.Append("{ \"serviceType\": \"");
        builder.Append(descriptor.ServiceType);
        builder.Append("\", \"lifetime\": \"");
        builder.Append(descriptor.Lifetime);
        builder.Append("\", ");

        if (descriptor.ImplementationType is not null)
        {
            builder.Append("\"implementationType\": \"");
            builder.Append(descriptor.ImplementationType);
        }
        else if (descriptor.ImplementationFactory is not null)
        {
            builder.Append("\"implementationFactory\": \"");
            builder.Append(descriptor.ImplementationFactory.Method);
        }
        else if (descriptor.ImplementationInstance is not null)
        {
            builder.Append("\"implementationInstance\": \"");
            builder.Append(descriptor.ImplementationInstance.GetType());
            builder.Append(" (instance)");
        }
        else
        {
            builder.Append("\"unknown\": \"");
        }

        builder.Append("\" }");
    }

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            // When this EventSource becomes enabled, write out the existing ServiceProvider information
            // because building the ServiceProvider happens early in the process. This way a listener
            // can get this information, even if they attach while the process is running.

            lock (_providers)
            {
                foreach (WeakReference<ServiceProvider> reference in _providers)
                {
                    if (reference.TryGetTarget(out ServiceProvider? provider))
                    {
                        WriteServiceProviderBuilt(provider);
                    }
                }
            }
        }
    }
}
