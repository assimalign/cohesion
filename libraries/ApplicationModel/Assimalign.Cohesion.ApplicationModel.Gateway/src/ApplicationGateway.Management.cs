using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.ApplicationModel.Internal;
using Assimalign.Cohesion.Hosting.Resources;

using HttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private const string jsonMediaType = "application/json";
    private static readonly TimeSpan _instanceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _eventTimeout = TimeSpan.FromSeconds(2);

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

    /// <inheritdoc />
    public async ValueTask<ResourceManagementResult> GetConfigurationAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default)
    {
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return UnknownResource(application, resource);
        }

        if (RecordStore is not IResourceRecordStore store)
        {
            return RecordsUnsupported();
        }

        ResourceRecord? record = await store.ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        return ResourceManagementResult.Create(
            ResourceManagementStatus.Ok,
            ResourceManagementDocuments.WriteConfiguration(application, resource, GetManifest(item).Surface, record),
            record?.Version);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceManagementResult> ChangeOverrideAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceSectionPath section,
        ResourceOverrideChangeKind kind,
        ReadOnlyMemory<byte> patch,
        string? expectedVersion,
        ApplicationCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return UnknownResource(application, resource);
        }

        if (RecordStore is not IResourceRecordStore store)
        {
            return RecordsUnsupported();
        }

        if (section.Value is null ||
            GetManifest(item).Surface is not ResourceSurface surface ||
            !surface.TryGetSection(section, out ResourceSurfaceSection? declared))
        {
            return Refuse(ResourceManagementStatus.UnknownSection, ErrorMessages.ManagementSectionUnknown, resource, section.Value ?? string.Empty);
        }

        // Decision D7: a backend section shapes data every instance shares, so only code, claims and actions change it.
        if (declared.Mutability == ResourceSurfaceMutability.Deployment)
        {
            return Refuse(ResourceManagementStatus.DeploymentOnly, ErrorMessages.ManagementSectionDeploymentOnly, resource, section.Value);
        }

        if (declared.Scope == ResourceSurfaceScope.Backend)
        {
            return Refuse(ResourceManagementStatus.NotOverridable, ErrorMessages.ManagementSectionNotOverridable, resource, section.Value);
        }

        JsonNode? value = null;
        if (kind != ResourceOverrideChangeKind.Remove)
        {
            if (!TryParseJson(patch, out JsonElement element, out value))
            {
                return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementBodyInvalid);
            }

            if (!declared.TryValidatePatch(element, out string? error))
            {
                return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementPatchRejected, section.Value, error);
            }
        }

        ResourceRecord? current = await store.ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Refuse(ResourceManagementStatus.Unavailable, ErrorMessages.ManagementRecordMissing, resource);
        }

        if (expectedVersion is not null && !string.Equals(expectedVersion, current.Version, StringComparison.Ordinal))
        {
            return Refuse(ResourceManagementStatus.VersionMoved, ErrorMessages.ManagementVersionMoved, resource, expectedVersion);
        }

        JsonObject layer = ParseObject(current.Override);
        switch (kind)
        {
            case ResourceOverrideChangeKind.Merge:
                // A null member is kept as a tombstone, so the override still removes it from the code value.
                layer[section.Value] = layer.TryGetPropertyValue(section.Value, out JsonNode? existing)
                    ? JsonMergePatchComposition.Compose(existing, value)
                    : value;
                break;
            case ResourceOverrideChangeKind.Replace:
                layer[section.Value] = value;
                break;
            default:
                layer.Remove(section.Value);
                break;
        }

        ResourceRecord next;
        try
        {
            next = ResourceRecord.Create(current.Generation, current.Deployment, current.Claims, ResourceManagementDocuments.WriteLayer(layer));
        }
        catch (InvalidDataException)
        {
            return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementLayerTooLarge);
        }

        if (!await store.TryWriteAsync(application, resource, next, current.Version, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(ResourceManagementStatus.VersionMoved, ErrorMessages.ManagementVersionMoved, resource, current.Version);
        }

        bool overridden = layer.TryGetPropertyValue(section.Value, out JsonNode? stored);
        await EmitManagementEventAsync(
                item.Model,
                resource,
                ResourceManagementOperations.WriteConfiguration,
                section.Value,
                caller,
                next.Generation,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ManagementOverrideChanged, caller.Subject, section.Value, resource, next.Generation),
                cancellationToken)
            .ConfigureAwait(false);
        return ResourceManagementResult.Create(
            ResourceManagementStatus.Ok,
            ResourceManagementDocuments.WriteOverride(section, overridden, stored, next),
            next.Version);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceManagementResult> ReplaceClaimsAsync(
        ApplicationName application,
        ResourceName resource,
        ApplicationName owner,
        ReadOnlyMemory<byte> entries,
        string? expectedVersion,
        ApplicationCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return UnknownResource(application, resource);
        }

        if (RecordStore is not IResourceRecordStore store)
        {
            return RecordsUnsupported();
        }

        JsonNode? parsed = null;
        if (!entries.IsEmpty && !TryParseJson(entries, out _, out parsed))
        {
            return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementBodyInvalid);
        }

        if (parsed is not null and not JsonObject)
        {
            return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementClaimsShape);
        }

        var claims = (JsonObject?)parsed ?? new JsonObject();
        ResourceSurface? surface = GetManifest(item).Surface;
        var sections = new List<string>(claims.Count);
        foreach ((string path, JsonNode? patch) in claims)
        {
            if (!ResourceSectionPath.IsValid(path, out _) ||
                surface is null ||
                !surface.TryGetSection(path, out ResourceSurfaceSection? declared))
            {
                return Refuse(ResourceManagementStatus.UnknownSection, ErrorMessages.ManagementSectionUnknown, resource, path);
            }

            if (!declared.Claimable)
            {
                return Refuse(ResourceManagementStatus.NotClaimable, ErrorMessages.ManagementSectionNotClaimable, resource, path);
            }

            // A claim adds keyed entries; it never removes one, so it is an object without null members.
            if (patch is not JsonObject keyed || keyed.Any(static entry => entry.Value is null))
            {
                return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementClaimShape, path);
            }

            using (JsonDocument document = JsonDocument.Parse(keyed.ToJsonString()))
            {
                if (!declared.TryValidatePatch(document.RootElement, out string? error))
                {
                    return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementPatchRejected, path, error);
                }
            }

            sections.Add(path);
        }

        ResourceRecord? current = await store.ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Refuse(ResourceManagementStatus.Unavailable, ErrorMessages.ManagementRecordMissing, resource);
        }

        if (expectedVersion is not null && !string.Equals(expectedVersion, current.Version, StringComparison.Ordinal))
        {
            return Refuse(ResourceManagementStatus.VersionMoved, ErrorMessages.ManagementVersionMoved, resource, expectedVersion);
        }

        if (FindClaimConflict(current, owner, claims) is ResourceManagementResult conflict)
        {
            return conflict;
        }

        var layers = new Dictionary<ApplicationName, ReadOnlyMemory<byte>>(current.Claims.Count + 1);
        foreach ((ApplicationName other, ReadOnlyMemory<byte> layer) in current.Claims)
        {
            if (other != owner)
            {
                layers.Add(other, layer);
            }
        }

        if (claims.Count > 0)
        {
            layers.Add(owner, ResourceManagementDocuments.WriteLayer(claims));
        }

        ResourceRecord next;
        try
        {
            next = ResourceRecord.Create(current.Generation, current.Deployment, layers, current.Override);
        }
        catch (InvalidDataException)
        {
            return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementLayerTooLarge);
        }

        if (!await store.TryWriteAsync(application, resource, next, current.Version, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(ResourceManagementStatus.VersionMoved, ErrorMessages.ManagementVersionMoved, resource, current.Version);
        }

        await EmitManagementEventAsync(
                item.Model,
                resource,
                ResourceManagementOperations.WriteClaims,
                owner.ToString(),
                caller,
                next.Generation,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ManagementClaimsChanged, caller.Subject, owner, resource, sections.Count),
                cancellationToken)
            .ConfigureAwait(false);
        return ResourceManagementResult.Create(
            ResourceManagementStatus.Ok,
            ResourceManagementDocuments.WriteClaims(owner, sections, next),
            next.Version);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceManagementResult> GetConfigurationStatusAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default)
    {
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return UnknownResource(application, resource);
        }

        if (RecordStore is not IResourceRecordStore store)
        {
            return RecordsUnsupported();
        }

        ResourceRecord? record = await store.ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        var reports = new List<InstanceStatusReport>();
        int desired;
        bool complete;
        if (TryGetResourceControlPlane(item.Model, item.Descriptor.Resource, out IResourceControlPlane? direct) && direct is not null)
        {
            desired = 1;
            complete = true;
            reports.Add(ReadStatus(direct));
        }
        else
        {
            ResourceInstanceInventory inventory;
            try
            {
                inventory = await ObserveResourceInstancesAsync(item.Model, item.Descriptor, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException exception)
            {
                return ResourceManagementResult.Refuse(ResourceManagementStatus.Unavailable, exception.Message);
            }

            desired = inventory.DesiredInstances;
            complete = inventory.IsComplete;
            foreach (ResourceManagementTarget target in inventory.Instances)
            {
                reports.Add(await ReadStatusAsync(item, target, cancellationToken).ConfigureAwait(false));
            }
        }

        return ResourceManagementResult.Create(
            ResourceManagementStatus.Ok,
            ResourceManagementDocuments.WriteStatus(application, resource, record, GetManifest(item).Surface, desired, complete, reports),
            record?.Version);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceManagementResult> InvokeActionAsync(
        ApplicationName application,
        ResourceName resource,
        string action,
        ReadOnlyMemory<byte> input,
        ApplicationCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return UnknownResource(application, resource);
        }

        if (GetManifest(item).Surface is not ResourceSurface surface || !surface.TryGetAction(action, out ResourceSurfaceAction? declared))
        {
            return Refuse(ResourceManagementStatus.UnknownAction, ErrorMessages.ManagementActionUnknown, resource, action);
        }

        byte[] body = input.IsEmpty ? "null"u8.ToArray() : input.ToArray();
        if (!TryParseJson(body, out JsonElement element, out _))
        {
            return Refuse(ResourceManagementStatus.InvalidInput, ErrorMessages.ManagementBodyInvalid);
        }

        if (!declared.TryValidateInput(element, out string? error))
        {
            return Refuse(ResourceManagementStatus.InvalidInput, ErrorMessages.ManagementActionInputRejected, action, error);
        }

        var outcomes = new List<InstanceActionOutcome>();
        if (TryGetResourceControlPlane(item.Model, item.Descriptor.Resource, out IResourceControlPlane? direct) && direct is not null)
        {
            outcomes.Add(await InvokeDirectAsync(direct, action, body, cancellationToken).ConfigureAwait(false));
        }
        else
        {
            ResourceInstanceInventory inventory;
            try
            {
                inventory = await ObserveResourceInstancesAsync(item.Model, item.Descriptor, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException exception)
            {
                return ResourceManagementResult.Refuse(ResourceManagementStatus.Unavailable, exception.Message);
            }

            foreach (ResourceManagementTarget target in inventory.Instances)
            {
                if (target.IsTerminating)
                {
                    continue;
                }

                InstanceActionOutcome outcome = await InvokeInstanceAsync(item, target, action, body, cancellationToken).ConfigureAwait(false);
                outcomes.Add(outcome);
                if (declared.Target == ResourceSurfaceActionTarget.AnyInstance && outcome.Succeeded)
                {
                    break;
                }
            }
        }

        int succeeded = outcomes.Count(static outcome => outcome.Succeeded);
        await EmitManagementEventAsync(
                item.Model,
                resource,
                ResourceManagementOperations.InvokeActions,
                action,
                caller,
                generation: null,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ManagementActionInvoked, caller.Subject, action, resource, succeeded, outcomes.Count),
                cancellationToken)
            .ConfigureAwait(false);
        return ResourceManagementResult.Create(
            succeeded == 0 ? ResourceManagementStatus.Unavailable : ResourceManagementStatus.Ok,
            ResourceManagementDocuments.WriteActionResults(action, declared.Target, outcomes));
    }

    /// <inheritdoc />
    public async ValueTask<ResourceManagementResult> GetInstanceInventoryAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default)
    {
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return UnknownResource(application, resource);
        }

        try
        {
            ResourceInstanceInventory inventory = await ObserveResourceInstancesAsync(item.Model, item.Descriptor, cancellationToken)
                .ConfigureAwait(false);
            return ResourceManagementResult.Create(ResourceManagementStatus.Ok, ResourceManagementDocuments.WriteInventory(inventory));
        }
        catch (NotSupportedException exception)
        {
            return ResourceManagementResult.Refuse(ResourceManagementStatus.Unavailable, exception.Message);
        }
    }

    /// <inheritdoc />
    public ValueTask<ResourceManagementResult> GetPlatformSurfaceAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceExtensionKey surface,
        CancellationToken cancellationToken = default)
    {
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return ValueTask.FromResult(UnknownResource(application, resource));
        }

        return ReadPlatformSurfaceAsync(item.Model, item.Descriptor, surface, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<ResourceManagementResult> ChangePlatformSurfaceAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceExtensionKey surface,
        ReadOnlyMemory<byte> patch,
        string? expectedVersion,
        ApplicationCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return ValueTask.FromResult(UnknownResource(application, resource));
        }

        return WritePlatformSurfaceAsync(item.Model, item.Descriptor, surface, patch, expectedVersion, caller, cancellationToken);
    }

    /// <summary>
    /// Reads a surface this gateway's platform publishes for a resource, such as a Kubernetes autoscaler's bounds. The
    /// default publishes none.
    /// </summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The resource's built descriptor.</param>
    /// <param name="surface">The platform-qualified surface key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The surface document, or <see cref="ResourceManagementStatus.UnknownPlatformSurface"/>.</returns>
    protected virtual ValueTask<ResourceManagementResult> ReadPlatformSurfaceAsync(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourceExtensionKey surface,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return ValueTask.FromResult(Refuse(
            ResourceManagementStatus.UnknownPlatformSurface, ErrorMessages.ManagementPlatformSurfaceUnknown, Name, descriptor.Resource.Name, surface.Value ?? string.Empty));
    }

    /// <summary>
    /// Changes a surface this gateway's platform publishes for a resource. The platform stores the change in its own
    /// deployment objects, under the same lifetime rule as configuration overrides. The default publishes none.
    /// </summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The resource's built descriptor.</param>
    /// <param name="surface">The platform-qualified surface key.</param>
    /// <param name="patch">The UTF-8 JSON merge patch.</param>
    /// <param name="expectedVersion">The version the caller read, or null to write unconditionally.</param>
    /// <param name="caller">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The changed surface document, or <see cref="ResourceManagementStatus.UnknownPlatformSurface"/>.</returns>
    protected virtual ValueTask<ResourceManagementResult> WritePlatformSurfaceAsync(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourceExtensionKey surface,
        ReadOnlyMemory<byte> patch,
        string? expectedVersion,
        ApplicationCaller caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return ValueTask.FromResult(Refuse(
            ResourceManagementStatus.UnknownPlatformSurface, ErrorMessages.ManagementPlatformSurfaceUnknown, Name, descriptor.Resource.Name, surface.Value ?? string.Empty));
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

    // A claimed entry has exactly one owner, and the resource's own deployment owns the entries it sets.
    private static ResourceManagementResult? FindClaimConflict(ResourceRecord current, ApplicationName owner, JsonObject claims)
    {
        JsonObject deployment = ParseObject(current.Deployment);
        foreach ((string section, JsonNode? entries) in claims)
        {
            foreach (string key in ((JsonObject)entries!).Select(static entry => entry.Key))
            {
                if (deployment[section] is JsonObject declared && declared.ContainsKey(key))
                {
                    return Refuse(ResourceManagementStatus.ClaimConflict, ErrorMessages.ManagementClaimConflictDeployment, section, key);
                }

                foreach ((ApplicationName other, ReadOnlyMemory<byte> layer) in current.Claims)
                {
                    if (other != owner && ParseObject(layer)[section] is JsonObject claimed && claimed.ContainsKey(key))
                    {
                        return Refuse(ResourceManagementStatus.ClaimConflict, ErrorMessages.ManagementClaimConflictOwner, section, key, other);
                    }
                }
            }
        }

        return null;
    }

    private static InstanceStatusReport ReadStatus(IResourceControlPlane controlPlane)
    {
        ResourceConfigurationStatus status = controlPlane.ConfigurationStatus;
        var sections = new List<InstanceSectionReport>(status.Sections.Count);
        foreach (ResourceConfigurationSectionStatus section in status.Sections)
        {
            sections.Add(new InstanceSectionReport(section.Path.Value, section.State switch
            {
                ResourceConfigurationState.Applied => "applied",
                ResourceConfigurationState.Rejected => "rejected",
                ResourceConfigurationState.RestartRequired => "restartRequired",
                _ => "baseline",
            }, section.Error));
        }

        return new InstanceStatusReport(null, null, true, null, controlPlane.Surface.Digest, status.RecordDigest, sections);
    }

    // GET <control plane>/configuration/status with the gateway's ResourceAccess credential, by the transport rule
    // every gateway call to a resource follows: HTTPS, or loopback HTTP in Local.
    private async ValueTask<InstanceStatusReport> ReadStatusAsync(ModelResource item, ResourceManagementTarget target, CancellationToken cancellationToken)
    {
        string address = target.Address.AbsoluteUri;
        try
        {
            using HttpResponseMessage response = await SendToInstanceAsync(item, target, HttpMethod.Get, "/configuration/status", null, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unreachable(address, string.Format(CultureInfo.CurrentCulture, ErrorMessages.ManagementInstanceRefused, (int)response.StatusCode));
            }

            byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            JsonElement configuration = root.GetProperty("configuration");
            var sections = new List<InstanceSectionReport>();
            foreach (JsonElement section in configuration.GetProperty("sections").EnumerateArray())
            {
                sections.Add(new InstanceSectionReport(
                    section.GetProperty("path").GetString()!,
                    section.GetProperty("state").GetString()!,
                    section.TryGetProperty("error", out JsonElement error) ? error.GetString() : null));
            }

            return new InstanceStatusReport(
                address,
                root.TryGetProperty("instance", out JsonElement instance) ? instance.GetString() : null,
                true,
                null,
                root.TryGetProperty("surfaceDigest", out JsonElement surface) ? surface.GetString() : null,
                configuration.TryGetProperty("recordDigest", out JsonElement digest) ? digest.GetString() : null,
                sections);
        }
        catch (Exception exception) when (IsInstanceFailure(exception, cancellationToken))
        {
            return Unreachable(address, exception.Message);
        }
    }

    private static async ValueTask<InstanceActionOutcome> InvokeDirectAsync(
        IResourceControlPlane controlPlane,
        string action,
        byte[] input,
        CancellationToken cancellationToken)
    {
        if (!ResourceActionName.TryCreate(action, out ResourceActionName name))
        {
            return new InstanceActionOutcome(null, false, null, null, ErrorMessages.ManagementInstanceActionUnknown);
        }

        try
        {
            ReadOnlyMemory<byte> result = await controlPlane.InvokeActionAsync(name, input, cancellationToken).ConfigureAwait(false);
            return new InstanceActionOutcome(null, true, null, ParseResult(result), null);
        }
        catch (NotSupportedException)
        {
            return new InstanceActionOutcome(null, false, null, null, ErrorMessages.ManagementInstanceActionUnknown);
        }
        catch (ArgumentException)
        {
            return new InstanceActionOutcome(null, false, null, null, ErrorMessages.ManagementInstanceInputRefused);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // An action is application code; whatever it throws is that instance's outcome, not the gateway's failure.
            return new InstanceActionOutcome(null, false, null, null, exception.GetType().FullName);
        }
    }

    private async ValueTask<InstanceActionOutcome> InvokeInstanceAsync(
        ModelResource item,
        ResourceManagementTarget target,
        string action,
        byte[] input,
        CancellationToken cancellationToken)
    {
        string address = target.Address.AbsoluteUri;
        try
        {
            using HttpResponseMessage response = await SendToInstanceAsync(
                    item, target, HttpMethod.Post, "/actions/" + Uri.EscapeDataString(action), input, cancellationToken)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;
            byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new InstanceActionOutcome(address, true, status, ParseResult(content), null)
                : new InstanceActionOutcome(address, false, status, null, status switch
                {
                    400 => ErrorMessages.ManagementInstanceInputRefused,
                    404 => ErrorMessages.ManagementInstanceActionUnknown,
                    _ => string.Format(CultureInfo.CurrentCulture, ErrorMessages.ManagementInstanceRefused, status),
                });
        }
        catch (Exception exception) when (IsInstanceFailure(exception, cancellationToken))
        {
            return new InstanceActionOutcome(address, false, null, null, exception.Message);
        }
    }

    private async Task<HttpResponseMessage> SendToInstanceAsync(
        ModelResource item,
        ResourceManagementTarget target,
        HttpMethod method,
        string route,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        if (!CanSendCredential(item.Model, target.Address, out string? failure))
        {
            throw new HttpRequestException(failure);
        }

        string token = await ((IResourceCommandCredentialProvider)this)
            .GetResourceCommandCredentialAsync(item.Model.Name, item.Descriptor.Resource.Name, cancellationToken)
            .ConfigureAwait(false);
        RemoteCertificateValidationCallback? validator =
            string.Equals(target.Address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? ((IResourceTransportTrustProvider)this).CreateOutboundTrustValidator(item.Model.Name)
                : null;
        using HttpMessageInvoker transport = GatewayHttpTransport.Create(validator);
        using var request = new HttpRequestMessage(
            method,
            new UriBuilder(target.Address) { Path = target.Address.AbsolutePath.TrimEnd('/') + route }.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(jsonMediaType);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_instanceTimeout);
        HttpResponseMessage response = await transport.SendAsync(request, timeout.Token).ConfigureAwait(false);
        // The content is buffered before the transport is disposed, so the caller reads it afterwards.
        await response.Content.LoadIntoBufferAsync(timeout.Token).ConfigureAwait(false);
        return response;
    }

    // Reports a management event to the application's telemetry sink, with the gateway itself as the emitter. Events
    // are best effort: an unreachable sink never fails the operation it reports.
    private async ValueTask EmitManagementEventAsync(
        IApplicationModel model,
        ResourceName resource,
        string operation,
        string? target,
        ApplicationCaller? caller,
        string? generation,
        string detail,
        CancellationToken cancellationToken)
    {
        try
        {
            ResourceTelemetryInjection? telemetry = await ResolveTelemetryAsync(model, Name, cancellationToken).ConfigureAwait(false);
            if (telemetry is null)
            {
                return;
            }

            byte[] payload = ManagementEventDocument.Write(
                Name, model.Name, resource, operation, target, caller?.Subject, generation, detail, _options.TimeProvider.GetUtcNow());
            RemoteCertificateValidationCallback? validator =
                string.Equals(telemetry.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? ((IResourceTransportTrustProvider)this).CreateOutboundTrustValidator(model.Name)
                    : null;
            using HttpMessageInvoker transport = GatewayHttpTransport.Create(validator);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new UriBuilder(telemetry.Endpoint) { Path = telemetry.Endpoint.AbsolutePath.TrimEnd('/') + "/v1/logs" }.Uri);
            foreach (string line in Encoding.UTF8.GetString(telemetry.HeadersDocument.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf(':');
                if (separator > 0)
                {
                    request.Headers.TryAddWithoutValidation(line[..separator].Trim(), line[(separator + 1)..].Trim());
                }
            }

            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(jsonMediaType);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_eventTimeout);
            using HttpResponseMessage _ = await transport.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The sink's own availability is reported by its health, not by the operation that tried to log to it.
        }
    }

    private bool TryFindRealizedResource(ApplicationName application, ResourceName resource, out ModelResource item)
    {
        foreach (ModelResource candidate in _order)
        {
            if (candidate.Model.Name == application && candidate.Descriptor.Resource.Name == resource && !IsExternalPlan(candidate.Descriptor.Plan!))
            {
                item = candidate;
                return true;
            }
        }

        item = default;
        return false;
    }

    private ModelResource FindRealizedResource(ApplicationName application, ResourceName resource) =>
        TryFindRealizedResource(application, resource, out ModelResource item)
            ? item
            : throw new NotSupportedException(ErrorMessages.ResourceNotRealized);

    private static ResourceManifest GetManifest(ModelResource item) =>
        item.Model.Manifests[IndexOfDescriptor(item.Model.Descriptors, item.Descriptor)];

    private static bool TryParseJson(ReadOnlyMemory<byte> content, out JsonElement element, out JsonNode? node)
    {
        try
        {
            using (JsonDocument document = JsonDocument.Parse(content))
            {
                element = document.RootElement.Clone();
            }

            node = JsonNode.Parse(content.Span);
            return true;
        }
        catch (JsonException)
        {
            element = default;
            node = null;
            return false;
        }
    }

    private static JsonObject ParseObject(ReadOnlyMemory<byte> layer) =>
        layer.IsEmpty ? new JsonObject() : JsonNode.Parse(layer.Span) as JsonObject ?? new JsonObject();

    private static JsonElement? ParseResult(ReadOnlyMemory<byte> result)
    {
        if (result.IsEmpty)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsInstanceFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException ||
        (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static InstanceStatusReport Unreachable(string address, string error) =>
        new(address, null, false, error, null, null, Array.Empty<InstanceSectionReport>());

    private ResourceManagementResult UnknownResource(ApplicationName application, ResourceName resource) =>
        Refuse(ResourceManagementStatus.UnknownResource, ErrorMessages.ManagementResourceUnknown, application, resource);

    private ResourceManagementResult RecordsUnsupported() =>
        Refuse(ResourceManagementStatus.RecordsUnsupported, ErrorMessages.ManagementRecordsUnsupported, Name);

    private static ResourceManagementResult Refuse(ResourceManagementStatus status, string message, params object?[] arguments) =>
        ResourceManagementResult.Refuse(status, string.Format(CultureInfo.CurrentCulture, message, arguments));
}
