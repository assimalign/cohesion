using System;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Diagnostics.CodeAnalysis;


namespace Assimalign.Cohesion.DependencyInjection;

using Assimalign.Cohesion.DependencyInjection.Internal;


[EventSource(Name = "Assimalign-Extensions-DependencyInjection")]
internal sealed class ServiceEventSource : EventSource
{
    public static readonly ServiceEventSource Log = new ServiceEventSource();
    public static class Keywords
    {
        public const EventKeywords ServiceProviderInitialized = (EventKeywords)0x1;
    }

    // Event source doesn't support large payloads so we chunk large payloads like formatted call site tree and descriptors
    private const int MaxChunkSize = 10 * 1024;

    private readonly List<WeakReference<ServiceProvider>> providers = new();

    private ServiceEventSource() : base(EventSourceSettings.EtwSelfDescribingEventFormat)
    {
    }

    // NOTE
    // - The 'Start' and 'Stop' suffixes on the following event names have special meaning in EventSource. They
    //   enable creating 'activities'.
    //   For more information, take a look at the following blog post:
    //   https://blogs.msdn.microsoft.com/vancem/2015/09/14/exploring-eventsource-activity-correlation-and-causation-features/
    // - A stop event's event id must be next one after its start event.
    // - Avoid renaming methods or parameters marked with EventAttribute. EventSource uses these to form the event object.

    [UnconditionalSuppressMessage(
        category: "ReflectionAnalysis", 
        checkId: "IL2026:RequiresUnreferencedCode",
        Justification = "Parameters to this method are primitive and are trimmer safe.")]
    [Event(1, Level = EventLevel.Verbose)]
    private void CallSiteBuilt(string serviceType, string callSite, int chunkIndex, int chunkCount, int serviceProviderHashCode)
    {
        WriteEvent(1, serviceType, callSite, chunkIndex, chunkCount, serviceProviderHashCode);
    }

    [Event(2, Level = EventLevel.Verbose)]
    public void ServiceResolved(string serviceType, int serviceProviderHashCode)
    {
        WriteEvent(2, serviceType, serviceProviderHashCode);
    }

    [Event(3, Level = EventLevel.Verbose)]
    public void ExpressionTreeGenerated(string serviceType, int nodeCount, int serviceProviderHashCode)
    {
        WriteEvent(3, serviceType, nodeCount, serviceProviderHashCode);
    }

    [Event(4, Level = EventLevel.Verbose)]
    public void DynamicMethodBuilt(string serviceType, int methodSize, int serviceProviderHashCode)
    {
        WriteEvent(4, serviceType, methodSize, serviceProviderHashCode);
    }

    [Event(5, Level = EventLevel.Verbose)]
    public void ScopeDisposed(int serviceProviderHashCode, int scopedServicesResolved, int disposableServices)
    {
        WriteEvent(5, serviceProviderHashCode, scopedServicesResolved, disposableServices);
    }

    [Event(6, Level = EventLevel.Error)]
    public void ServiceRealizationFailed(string? exceptionMessage, int serviceProviderHashCode)
    {
        WriteEvent(6, exceptionMessage, serviceProviderHashCode);
    }

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode",
        Justification = "Parameters to this method are primitive and are trimmer safe.")]
    [Event(7, Level = EventLevel.Informational, Keywords = Keywords.ServiceProviderInitialized)]
    private void ServiceProviderBuilt(int serviceProviderHashCode, int singletonServices, int scopedServices, int transientServices, int closedGenericsServices, int openGenericsServices)
    {
        WriteEvent(7, serviceProviderHashCode, singletonServices, scopedServices, transientServices, closedGenericsServices, openGenericsServices);
    }

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode",
        Justification = "Parameters to this method are primitive and are trimmer safe.")]
    [Event(8, Level = EventLevel.Informational, Keywords = Keywords.ServiceProviderInitialized)]
    private void ServiceProviderDescriptors(int serviceProviderHashCode, string descriptors, int chunkIndex, int chunkCount)
    {
        WriteEvent(8, serviceProviderHashCode, descriptors, chunkIndex, chunkCount);
    }

    [NonEvent]
    public void ServiceResolved(ServiceProvider provider, Type serviceType)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.All))
        {
            ServiceResolved(serviceType.ToString(), provider.GetHashCode());
        }
    }

    [NonEvent]
    public void CallSiteBuilt(ServiceProvider provider, Type serviceType, CallSiteService callSite)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.All))
        {
            var format = CallSiteJsonFormatterVisitor.Instance.Format(callSite);
            var chunkCount = format.Length / MaxChunkSize + (format.Length % MaxChunkSize > 0 ? 1 : 0);
            var providerHashCode = provider.GetHashCode();
            for (int i = 0; i < chunkCount; i++)
            {
                CallSiteBuilt(
                    serviceType.ToString(),
                    format.Substring(i * MaxChunkSize, Math.Min(MaxChunkSize, format.Length - i * MaxChunkSize)), i, chunkCount,
                    providerHashCode);
            }
        }
    }

    [NonEvent]
    public void DynamicMethodBuilt(ServiceProvider provider, Type serviceType, int methodSize)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.All))
        {
            DynamicMethodBuilt(serviceType.ToString(), methodSize, provider.GetHashCode());
        }
    }

    [NonEvent]
    public void ServiceRealizationFailed(Exception exception, int serviceProviderHashCode)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.All))
        {
            ServiceRealizationFailed(exception.ToString(), serviceProviderHashCode);
        }
    }

    [NonEvent]
    public void ServiceProviderBuilt(ServiceProvider provider)
    {
        lock (providers)
        {
            providers.Add(new WeakReference<ServiceProvider>(provider));
        }

        WriteServiceProviderBuilt(provider);
    }

    [NonEvent]
    public void ServiceProviderDisposed(ServiceProvider provider)
    {
        lock (providers)
        {
            for (int i = providers.Count - 1; i >= 0; i--)
            {
                // remove the provider, along with any stale references
                WeakReference<ServiceProvider> reference = providers[i];
                if (!reference.TryGetTarget(out ServiceProvider target) || target == provider)
                {
                    providers.RemoveAt(i);
                }
            }
        }
    }

    [NonEvent]
    private void WriteServiceProviderBuilt(ServiceProvider provider)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.ServiceProviderInitialized))
        {
            int singletonServices = 0;
            int scopedServices = 0;
            int transientServices = 0;
            int closedGenericsServices = 0;
            int openGenericsServices = 0;

            StringBuilder descriptorBuilder = new StringBuilder("{ \"descriptors\":[ ");
            bool firstDescriptor = true;
            foreach (ServiceDescriptor descriptor in provider.CallSiteFactory.Descriptors)
            {
                if (firstDescriptor)
                {
                    firstDescriptor = false;
                }
                else
                {
                    descriptorBuilder.Append(", ");
                }

                AppendServiceDescriptor(descriptorBuilder, descriptor);

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
            descriptorBuilder.Append(" ] }");

            int providerHashCode = provider.GetHashCode();
            ServiceProviderBuilt(providerHashCode, singletonServices, scopedServices, transientServices, closedGenericsServices, openGenericsServices);

            string descriptorString = descriptorBuilder.ToString();
            int chunkCount = descriptorString.Length / MaxChunkSize + (descriptorString.Length % MaxChunkSize > 0 ? 1 : 0);

            for (int i = 0; i < chunkCount; i++)
            {
                ServiceProviderDescriptors(
                    providerHashCode,
                    descriptorString.Substring(i * MaxChunkSize, Math.Min(MaxChunkSize, descriptorString.Length - i * MaxChunkSize)), i, chunkCount);
            }
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

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            // When this EventSource becomes enabled, write out the existing ServiceProvider information
            // because building the ServiceProvider happens early in the process. This way a listener
            // can get this information, even if they attach while the process is running.

            lock (providers)
            {
                foreach (WeakReference<ServiceProvider> reference in providers)
                {
                    if (reference.TryGetTarget(out ServiceProvider provider))
                    {
                        WriteServiceProviderBuilt(provider);
                    }
                }
            }
        }
    }
}

