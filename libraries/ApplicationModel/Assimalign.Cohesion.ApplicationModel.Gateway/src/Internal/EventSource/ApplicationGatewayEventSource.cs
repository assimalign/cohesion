using System;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// The gateway's diagnostics: one event per transition of a run and of each resource's step, so a
/// deployment can be followed as it happens rather than read from the run's final exception.
/// </summary>
/// <remarks>
/// Tools enable it by its assembly name, <c>Assimalign.Cohesion.ApplicationModel.Gateway</c>; a gateway
/// program forwards it into its logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Every
/// event names its gateway, and a resource as <c>{provider}/{name}</c>.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.ApplicationModel.Gateway")]
internal sealed class ApplicationGatewayEventSource : EventSource
{
    public static readonly ApplicationGatewayEventSource Log = new();

    private ApplicationGatewayEventSource()
    {
    }

    [NonEvent]
    public void RunStarting(GatewayName gateway, long epoch)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            RunStarting(gateway.Value, epoch);
        }
    }

    [NonEvent]
    public void ResourcePlanned(GatewayName gateway, ApplicationResourceManifest resource, InfrastructureChange change)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ResourcePlanned(gateway.Value, Describe(resource), change.ToString());
        }
    }

    [NonEvent]
    public void ResourceDeploying(GatewayName gateway, ApplicationResourceManifest resource, DeploymentId deployment)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ResourceDeploying(gateway.Value, Describe(resource), deployment.ToString());
        }
    }

    [NonEvent]
    public void ResourceReady(GatewayName gateway, ApplicationResourceManifest resource)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ResourceReady(gateway.Value, Describe(resource));
        }
    }

    [NonEvent]
    public void ResourceMissed(GatewayName gateway, ApplicationResourceManifest resource, TimeSpan window)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ResourceMissed(gateway.Value, Describe(resource), window.TotalSeconds);
        }
    }

    [NonEvent]
    public void ResourceStopped(GatewayName gateway, ApplicationResourceManifest resource, Exception? cause)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ResourceStopped(gateway.Value, Describe(resource), cause?.GetType().FullName ?? "ended", cause?.Message ?? string.Empty);
        }
    }

    [NonEvent]
    public void ResourceSkipped(GatewayName gateway, ApplicationResourceManifest resource)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ResourceSkipped(gateway.Value, Describe(resource));
        }
    }

    [NonEvent]
    public void ResourceRetired(GatewayName gateway, ProviderName provider, ResourceName name)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ResourceRetired(gateway.Value, string.Concat(provider.Value, "/", name.Value));
        }
    }

    [NonEvent]
    public void RunStarted(GatewayName gateway, int resources)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            RunStarted(gateway.Value, resources);
        }
    }

    [NonEvent]
    public void RunSuperseded(GatewayName gateway, long epoch)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            RunSuperseded(gateway.Value, epoch);
        }
    }

    [NonEvent]
    public void RunStopped(GatewayName gateway)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            RunStopped(gateway.Value);
        }
    }

    [NonEvent]
    public void RunFailed(GatewayName gateway, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            RunFailed(gateway.Value, exception.GetType().FullName ?? string.Empty, exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Informational, Message = "Gateway {0} took its record over at epoch {1}.")]
    private void RunStarting(string gateway, long epoch) => WriteEvent(1, gateway, epoch);

    [Event(2, Level = EventLevel.Informational, Message = "Gateway {0} planned {1}: {2}.")]
    private void ResourcePlanned(string gateway, string resource, string change) => WriteEvent(2, gateway, resource, change);

    [Event(3, Level = EventLevel.Informational, Message = "Gateway {0} is deploying {1} as deployment {2}.")]
    private void ResourceDeploying(string gateway, string resource, string deployment) => WriteEvent(3, gateway, resource, deployment);

    [Event(4, Level = EventLevel.Informational, Message = "Gateway {0}: {1} is ready.")]
    private void ResourceReady(string gateway, string resource) => WriteEvent(4, gateway, resource);

    [Event(5, Level = EventLevel.Error, Message = "Gateway {0}: {1} published no manifest within {2} seconds.")]
    private void ResourceMissed(string gateway, string resource, double windowSeconds) => WriteEvent(5, gateway, resource, windowSeconds);

    [Event(6, Level = EventLevel.Error, Message = "Gateway {0}: {1} stopped before an instance published: {2} {3}")]
    private void ResourceStopped(string gateway, string resource, string failure, string failureMessage) => WriteEvent(6, gateway, resource, failure, failureMessage);

    [Event(7, Level = EventLevel.Warning, Message = "Gateway {0}: {1} was not deployed, because something it depends on did not start.")]
    private void ResourceSkipped(string gateway, string resource) => WriteEvent(7, gateway, resource);

    [Event(8, Level = EventLevel.Informational, Message = "Gateway {0} tore down {1}, which its model no longer declares.")]
    private void ResourceRetired(string gateway, string resource) => WriteEvent(8, gateway, resource);

    [Event(9, Level = EventLevel.Informational, Message = "Gateway {0} started with {1} resources.")]
    private void RunStarted(string gateway, int resources) => WriteEvent(9, gateway, resources);

    [Event(10, Level = EventLevel.Warning, Message = "Gateway {0} stopped: a later run took its record over from epoch {1}.")]
    private void RunSuperseded(string gateway, long epoch) => WriteEvent(10, gateway, epoch);

    [Event(11, Level = EventLevel.Informational, Message = "Gateway {0} stopped.")]
    private void RunStopped(string gateway) => WriteEvent(11, gateway);

    [Event(12, Level = EventLevel.Error, Message = "Gateway {0} failed: {1}: {2}")]
    private void RunFailed(string gateway, string failure, string failureMessage) => WriteEvent(12, gateway, failure, failureMessage);

    private static string Describe(ApplicationResourceManifest resource)
    {
        return string.Concat(resource.Provider.Value, "/", resource.Name.Value);
    }
}
