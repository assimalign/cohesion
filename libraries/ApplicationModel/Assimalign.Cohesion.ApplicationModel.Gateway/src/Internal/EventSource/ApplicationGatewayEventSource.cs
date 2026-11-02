using System;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// The gateway's diagnostics: one event per transition of a run and of each resource's step, so a
/// deployment can be followed as it happens rather than read from the run's final exception, and one
/// per operator action, recorded write, and instance the gateway could not call.
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
    public void ResourcePlanned(GatewayName gateway, ApplicationResourceManifest resource, ApplicationGatewayDeploymentChange change)
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
            ResourceRetired(gateway.Value, Describe(provider, name));
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

    [NonEvent]
    public void ActionExecuted(GatewayName gateway, ProviderName provider, ResourceName name, ActionName action, ApplicationActionOperation operation, InstanceKey instance, ApplicationActionStatus status)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ActionExecuted(gateway.Value, Describe(provider, name), action.Value, operation.ToString(), instance.ToString(), status.ToString());
        }
    }

    [NonEvent]
    public void StateRecorded(GatewayName gateway, ProviderName provider, ResourceName name, ActionName action, InstanceKey instance, int restores)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            StateRecorded(gateway.Value, Describe(provider, name), action.Value, instance.ToString(), restores);
        }
    }

    // A refusal's reason is left out: a control plane's rejection can echo the content it refused.
    [NonEvent]
    public void InstanceDropped(GatewayName gateway, ProviderName provider, ResourceName name, InstanceKey instance, Exception? failure, ApplicationActionStatus? refusal)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            string kind = failure?.GetType().FullName ?? refusal?.ToString() ?? "NoControlPlane";
            InstanceDropped(gateway.Value, Describe(provider, name), instance.ToString(), kind, failure?.Message ?? string.Empty);
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

    [Event(13, Level = EventLevel.Informational, Message = "Gateway {0} ran {3} of {2} on {1} instance {4}: {5}.")]
    private void ActionExecuted(string gateway, string resource, string action, string operation, string instance, string status) => WriteEvent(13, gateway, resource, action, operation, instance, status);

    [Event(14, Level = EventLevel.Informational, Message = "Gateway {0} recorded the {2} write on {1} instance {3} and restores it on {4} other instances.")]
    private void StateRecorded(string gateway, string resource, string action, string instance, int restores) => WriteEvent(14, gateway, resource, action, instance, restores);

    [Event(15, Level = EventLevel.Warning, Message = "Gateway {0} unregistered {1} instance {2}, which it could not call: {3} {4}. The instance publishes again when it next renews.")]
    private void InstanceDropped(string gateway, string resource, string instance, string failure, string failureMessage) => WriteEvent(15, gateway, resource, instance, failure, failureMessage);

    private static string Describe(ApplicationResourceManifest resource)
    {
        return string.Concat(resource.Provider.Value, "/", resource.Name.Value);
    }

    private static string Describe(ProviderName provider, ResourceName name)
    {
        return string.Concat(provider.Value, "/", name.Value);
    }
}
