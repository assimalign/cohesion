using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceStateProjectionRunner")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceStateProjectionRunner : IHostRunner
{
    private readonly IHostRunner _inner;
    private readonly IResourceControlPlane _plane;
    private readonly ResourceMount _mount;

    public ResourceStateProjectionRunner(IHostRunner inner, IResourceControlPlane plane, ResourceMount mount)
    {
        _inner = inner;
        _plane = plane;
        _mount = mount;
    }

    public async Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task projection = ProjectAsync(stop.Token);
        try { await _inner.RunAsync(run, cancellationToken).ConfigureAwait(false); }
        finally
        {
            stop.Cancel();
            await projection.ConfigureAwait(false);
        }
    }

    private async Task ProjectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                byte[] content = _mount.ReadAllBytes();
                await ApplyProjectionAsync(_plane, content, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException or FormatException)
            {
                // A missing, incompatible or not-yet-applicable projection must keep bootstrap closed.
                // Existing valid feature state remains active; the next platform projection is retried.
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    internal static async ValueTask<bool> ApplyProjectionAsync(IResourceControlPlane plane, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        if (content.Length > 1024 * 1024) { throw new InvalidDataException(ErrorMessages.ResourceRuntimeInvalid); }
        using JsonDocument document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;
        Guid incarnation = root.GetProperty("incarnation").GetGuid();
        if (root.GetProperty("schema").GetString() != "cohesion/resource-state-projection/v1" || incarnation == Guid.Empty)
        {
            throw new InvalidDataException(ErrorMessages.ResourceRuntimeInvalid);
        }
        ResourceRuntimeManifest runtime = plane.RuntimeManifest ?? throw new InvalidDataException(ErrorMessages.ResourceRuntimeInvalid);
        var entries = new List<(ResourceStateName Name, ResourceStateKey Key, long Revision, string Digest, long Fence,
            ResourceStateScope Scope, ResourceStateValue Desired)>();
        var slots = new HashSet<(ResourceStateName, ResourceStateKey)>();
        foreach (JsonElement entry in root.GetProperty("entries").EnumerateArray())
        {
            ResourceStateName name = entry.GetProperty("name").GetString()!;
            ResourceStateKey key = entry.GetProperty("key").GetString()!;
            ResourceStateDefinition? definition = runtime.States.FirstOrDefault(state => state.Name == name);
            if (definition is null || !slots.Add((name, key)) || definition.ContractDigest != entry.GetProperty("contractDigest").GetString() ||
                entry.GetProperty("scope").GetString() != (definition.Scope == ResourceStateScope.EveryInstance ? "everyInstance" : "sharedState")) { return false; }
            long revision = entry.GetProperty("revision").GetInt64();
            long fence = entry.GetProperty("fence").GetInt64();
            string digest = entry.GetProperty("valueDigest").GetString()!;
            JsonElement desired = entry.GetProperty("desired");
            var value = new ResourceStateValue(desired.GetProperty("exists").GetBoolean(), desired.GetProperty("value").Clone());
            if (revision <= 0 || fence <= 0 || digest != ResourceStateWire.ComputeValueDigest(value.Exists, value.Value) ||
                !(await plane.ValidateStateAsync(name, key, value, cancellationToken).ConfigureAwait(false)).IsValid) { return false; }
            entries.Add((name, key, revision, digest, fence, definition.Scope, value));
        }
        // Validate the entire projection before changing any feature or considering startup defaults.
        var admissions = new List<ResourceStateAdmissionSlot>();
        foreach (var entry in entries)
        {
            if (entry.Scope == ResourceStateScope.EveryInstance)
            {
                ResourceStateApplyResult applied = await plane.ApplyStateAsync(
                    new ResourceStateApplyContext(incarnation, plane.ProcessStartId, entry.Revision, entry.Digest, entry.Fence),
                    entry.Name, entry.Key, entry.Desired, cancellationToken).ConfigureAwait(false);
                if (applied.Status is not (ResourceStateApplyStatus.Applied or ResourceStateApplyStatus.AlreadyCurrent)) { return false; }
            }
            admissions.Add(new ResourceStateAdmissionSlot(entry.Name, entry.Key, entry.Revision, entry.Digest));
        }
        foreach (ResourceStateDeclaration configured in runtime.InitialState)
        {
            if (!slots.Add((configured.Name, configured.Key))) { continue; }
            admissions.Add(new ResourceStateAdmissionSlot(configured.Name, configured.Key, 0,
                ResourceStateWire.ComputeValueDigest(configured.Desired.Exists, configured.Desired.Value)));
        }
        return await plane.AdmitStateAsync(new ResourceStateAdmission(plane.ProcessStartId, admissions), cancellationToken).ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        public DebugView(ResourceStateProjectionRunner value) { }
    }
}
