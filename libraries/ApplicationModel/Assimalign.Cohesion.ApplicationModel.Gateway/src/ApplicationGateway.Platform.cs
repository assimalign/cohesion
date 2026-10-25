using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

// Platform surfaces and record delivery: the hooks a platform gateway overrides to publish the manageable settings of
// its own objects and to deliver what its resources cannot read for themselves.
public abstract partial class ApplicationGateway
{
    /// <inheritdoc />
    public async ValueTask<ResourceManagementResult> GetPlatformSurfaceAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceExtensionKey surface,
        CancellationToken cancellationToken = default)
    {
        if (!TryFindRealizedResource(application, resource, out ModelResource item))
        {
            return UnknownResource(application, resource);
        }

        if (FindPlatformSurface(item, surface) is not ResourcePlatformSurface published)
        {
            return UnknownPlatformSurface(item, surface);
        }

        ResourceRecord? record = RecordStore is IResourceRecordStore store
            ? await store.ReadAsync(application, resource, cancellationToken).ConfigureAwait(false)
            : null;
        JsonElement? @override = ReadPlatformOverride(record, published.Key);
        JsonElement? observed = await ObservePlatformSurfaceAsync(item.Model, item.Descriptor, published, cancellationToken)
            .ConfigureAwait(false);
        return ResourceManagementResult.Create(
            ResourceManagementStatus.Ok,
            ResourceManagementDocuments.WritePlatformSurface(
                application, resource, published, record, @override, published.Apply(@override), observed),
            record?.Version);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceManagementResult> ChangePlatformSurfaceAsync(
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
            return UnknownResource(application, resource);
        }

        if (FindPlatformSurface(item, surface) is not ResourcePlatformSurface published)
        {
            return UnknownPlatformSurface(item, surface);
        }

        if (RecordStore is not IResourceRecordStore store)
        {
            return RecordsUnsupported();
        }

        if (!TryParseJson(patch, out _, out JsonNode? incoming))
        {
            return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementBodyInvalid);
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

        // A PATCH merges into the generation's override; a JSON null drops the override, so the code value returns.
        var platform = new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>>(current.PlatformOverrides);
        JsonElement? stored = null;
        if (incoming is null)
        {
            platform.Remove(published.Key);
        }
        else
        {
            JsonNode? existing = current.PlatformOverrides.TryGetValue(published.Key, out ReadOnlyMemory<byte> layer)
                ? JsonNode.Parse(layer.Span)
                : null;
            JsonNode? composed = existing is null ? incoming : JsonMergePatchComposition.Compose(existing, incoming);
            using JsonDocument document = JsonDocument.Parse(composed?.ToJsonString() ?? "null");
            if (!published.TryValidatePatch(document.RootElement, out string? error))
            {
                return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementPlatformPatchRejected, published.Key, error);
            }

            stored = document.RootElement.Clone();
            platform[published.Key] = Encoding.UTF8.GetBytes(stored.Value.GetRawText());
        }

        ResourceRecord next;
        try
        {
            next = ResourceRecord.Create(current.Generation, current.Deployment, current.Claims, current.Override, platform);
        }
        catch (InvalidDataException)
        {
            return Refuse(ResourceManagementStatus.InvalidConfiguration, ErrorMessages.ManagementLayerTooLarge);
        }

        if (!await store.TryWriteAsync(application, resource, next, current.Version, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(ResourceManagementStatus.VersionMoved, ErrorMessages.ManagementVersionMoved, resource, current.Version);
        }

        JsonElement value = published.Apply(stored);
        try
        {
            await ApplyPlatformSurfaceAsync(item.Model, item.Descriptor, published, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The override is the desired state and is stored; the platform converges on it when it next reconciles.
            await ReportDeliveryFailureAsync(item, next, published.Key.Value, exception, cancellationToken).ConfigureAwait(false);
        }

        await EmitManagementEventAsync(
                item.Model,
                resource,
                ResourceManagementOperations.WritePlatform,
                published.Key.Value,
                caller,
                next.Generation,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ManagementPlatformOverrideChanged, caller.Subject, published.Key, resource, next.Generation),
                cancellationToken)
            .ConfigureAwait(false);
        JsonElement? observed = await ObservePlatformSurfaceAsync(item.Model, item.Descriptor, published, cancellationToken)
            .ConfigureAwait(false);
        return ResourceManagementResult.Create(
            ResourceManagementStatus.Ok,
            ResourceManagementDocuments.WritePlatformSurface(application, resource, published, next, stored, value, observed),
            next.Version);
    }

    /// <summary>
    /// Lists the surfaces this gateway's platform publishes for a resource, each with the value the application's
    /// code declares. The default publishes none.
    /// </summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The resource's built descriptor; its plan carries the declared values.</param>
    /// <returns>The surfaces, each under a key whose platform part is this gateway's <see cref="Name"/>.</returns>
    /// <remarks>
    /// The management API serves each at <c>platform/{platform}/{name}</c>. Overrides an operator writes are kept with
    /// the resource's deployment record for the current generation, reach the compiler as
    /// <see cref="ResourceInputs.PlatformOverrides"/>, and reach the running platform through
    /// <see cref="ApplyPlatformSurfaceAsync"/>.
    /// </remarks>
    protected virtual IReadOnlyList<ResourcePlatformSurface> GetPlatformSurfaces(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor) =>
        Array.Empty<ResourcePlatformSurface>();

    /// <summary>
    /// Puts a platform surface's value in force on the platform's objects after an operator changed its override. The
    /// default does nothing.
    /// </summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The resource's built descriptor.</param>
    /// <param name="surface">The surface, with the value code declares.</param>
    /// <param name="value">The value now in force: the declared value with the stored override applied.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task that completes when the platform accepted the value.</returns>
    /// <remarks>
    /// The override is already stored when this runs. A failure is reported as a management event and on standard
    /// error, and the platform's next reconcile of the resource applies the stored value.
    /// </remarks>
    protected virtual ValueTask ApplyPlatformSurfaceAsync(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourcePlatformSurface surface,
        JsonElement value,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Reads what the platform observes for one of its surfaces, such as an autoscaler's current replica count. The
    /// default observes nothing.
    /// </summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The resource's built descriptor.</param>
    /// <param name="surface">The surface.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A JSON value the management API returns as <c>observed</c>, or <see langword="null"/> for none.</returns>
    protected virtual ValueTask<JsonElement?> ObservePlatformSurfaceAsync(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourcePlatformSurface surface,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<JsonElement?>(null);

    /// <summary>
    /// Delivers a management change of a resource's record that its instances cannot read for themselves. The default
    /// does nothing, which is right for a platform whose instances read the store directly.
    /// </summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The resource's built descriptor.</param>
    /// <param name="previous">The record before the change.</param>
    /// <param name="current">The record now stored.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    /// <returns>A task that completes when the change is delivered.</returns>
    /// <remarks>
    /// <para>
    /// Called after an override or claims write, never for a deployment: a deployment delivers the record when the
    /// controller realizes the resource. A platform that copies the record into its instances copies it here, and a
    /// platform that restarts instances for sections applied only at startup compares
    /// <see cref="ResourceRecord.GetRestartDigest"/> of the two records.
    /// </para>
    /// <para>
    /// The change is already stored when this runs. A failure is reported as a management event and on standard
    /// error; the record still reaches the instances at their next start.
    /// </para>
    /// </remarks>
    protected virtual ValueTask OnRecordChangedAsync(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourceRecord previous,
        ResourceRecord current,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>Gets the runtime surface a resource's build described, as its manifest in the model carries it.</summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The resource's descriptor in <paramref name="model"/>.</param>
    /// <returns>The surface, or <see langword="null"/> when the resource's build described none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> or <paramref name="descriptor"/> is null.</exception>
    protected static ResourceSurface? GetRuntimeSurface(IApplicationModel model, IApplicationResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(descriptor);
        return model.Manifests[IndexOfDescriptor(model.Descriptors, descriptor)].Surface;
    }

    private async ValueTask NotifyRecordChangedAsync(
        ModelResource item,
        ResourceRecord previous,
        ResourceRecord current,
        CancellationToken cancellationToken)
    {
        try
        {
            await OnRecordChangedAsync(item.Model, item.Descriptor, previous, current, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await ReportDeliveryFailureAsync(item, current, target: null, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ReportDeliveryFailureAsync(
        ModelResource item,
        ResourceRecord record,
        string? target,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // The exception type is reported, not its message: a platform client's message can carry request content.
        string detail = string.Format(
            CultureInfo.CurrentCulture,
            ErrorMessages.ManagementDeliveryFailed,
            item.Descriptor.Resource.Name,
            target ?? "record",
            exception.GetType().FullName);
        Console.Error.WriteLine(detail);
        await EmitManagementEventAsync(
                item.Model,
                item.Descriptor.Resource.Name,
                ResourceManagementOperations.DeliverRecord,
                target ?? "record",
                caller: null,
                record.Generation,
                detail,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ResourcePlatformSurface? FindPlatformSurface(ModelResource item, ResourceExtensionKey key)
    {
        if (key.Value is null)
        {
            return null;
        }

        foreach (ResourcePlatformSurface surface in GetPlatformSurfaces(item.Model, item.Descriptor))
        {
            if (surface.Key == key)
            {
                return surface;
            }
        }

        return null;
    }

    private ResourceManagementResult UnknownPlatformSurface(ModelResource item, ResourceExtensionKey key) =>
        Refuse(ResourceManagementStatus.UnknownPlatformSurface, ErrorMessages.ManagementPlatformSurfaceUnknown,
            Name, item.Descriptor.Resource.Name, key.Value ?? string.Empty);

    private static JsonElement? ReadPlatformOverride(ResourceRecord? record, ResourceExtensionKey key)
    {
        if (record is null || !record.PlatformOverrides.TryGetValue(key, out ReadOnlyMemory<byte> patch))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(patch);
        return document.RootElement.Clone();
    }
}
