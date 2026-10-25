using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    /// <summary>Lists the individually addressable instances of a resource this gateway realizes.</summary>
    /// <remarks>
    /// Each instance's address is its own control plane, so a management client can read the configuration
    /// status every instance reports and run an action on one instance or on all of them.
    /// </remarks>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The platform's current inventory, including instances that are still starting.</returns>
    /// <exception cref="NotSupportedException">
    /// This gateway does not realize the resource, or the platform runs several instances it cannot observe individually.
    /// </exception>
    public ValueTask<ResourceInstanceInventory> GetInstancesAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        ModelResource item = FindRealizedResource(application, resource);
        return ObserveResourceInstancesAsync(item.Model, item.Descriptor, cancellationToken);
    }

    /// <summary>Observes the resource's individually addressable control planes. A platform that runs several instances overrides this hook.</summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The logical resource descriptor.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The current platform inventory, including instances that are still starting.</returns>
    /// <exception cref="NotSupportedException">The plan runs several instances and this gateway cannot observe them individually.</exception>
    protected virtual ValueTask<ResourceInstanceInventory> ObserveResourceInstancesAsync(IApplicationModel model,
        IApplicationResourceDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        int desired = descriptor.Plan!.Workload.Replicas;
        if (desired > 1)
        {
            throw new NotSupportedException(ErrorMessages.ResourceInstancesUnobservable);
        }

        var targets = new List<ResourceManagementTarget>();
        if (TryGetResourceManagementBinding(model, descriptor, out PortBinding? binding, out string? path))
        {
            foreach (ResourceEndpoint endpoint in GetApplicationState(model).GetObservedEndpoints(descriptor.Resource.Id))
            {
                if (endpoint.Name == binding!.Endpoint && Uri.TryCreateEndpoint(endpoint.Scheme, endpoint.Host, endpoint.Port, path, out Uri? address))
                {
                    targets.Add(new ResourceManagementTarget(descriptor.Resource.Name.Value, address.AbsoluteUri, address));
                }
            }
        }

        return ValueTask.FromResult(new ResourceInstanceInventory(CreateIncarnation(model, descriptor),
            string.Join("|", targets.Select(static target => target.Generation)), desired, targets.Count == desired, targets));
    }

    /// <summary>Resolves the plan's control-plane endpoint to its declared port and path, without resource-area knowledge.</summary>
    /// <param name="model">The owning application.</param>
    /// <param name="descriptor">The resource descriptor.</param>
    /// <param name="binding">The declared control-plane port when present.</param>
    /// <param name="path">The control-plane base path when the port is present.</param>
    /// <returns>Whether the plan publishes its control-plane endpoint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> or <paramref name="descriptor"/> is null.</exception>
    protected static bool TryGetResourceManagementBinding(IApplicationModel model, IApplicationResourceDescriptor descriptor,
        out PortBinding? binding, out string? path)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(descriptor);
        ControlPlaneSpec controlPlane = descriptor.Plan!.ControlPlane;
        binding = descriptor.Plan.Container.Ports.FirstOrDefault(port => port.Endpoint == controlPlane.Endpoint);
        path = binding is null ? null : controlPlane.Path;
        return binding is not null;
    }

    /// <summary>Derives the deployment identity the inventory reports, stable for an application and resource name.</summary>
    /// <param name="model">The owning application.</param>
    /// <param name="descriptor">The resource descriptor.</param>
    /// <returns>A name-based identity; a platform with a native deployment identity reports that instead.</returns>
    protected static Guid CreateIncarnation(IApplicationModel model, IApplicationResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(descriptor);
        byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes(model.Name.Value + "\0" + descriptor.Resource.Name.Value));
        return new Guid(identity.AsSpan(0, 16));
    }

    private ModelResource FindRealizedResource(ApplicationName application, ResourceName resource)
    {
        foreach (ModelResource item in _order)
        {
            if (item.Model.Name == application && item.Descriptor.Resource.Name == resource && !IsExternalPlan(item.Descriptor.Plan!))
            {
                return item;
            }
        }

        throw new NotSupportedException(ErrorMessages.ResourceNotRealized);
    }
}
