using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceManagementCoordinator")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed partial class ResourceManagementCoordinator
{
    private readonly IResourceManagementStore _store;
    private readonly IResourceStateProtocolClient _protocol;
    private readonly TimeProvider _clock;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Func<ApplicationName, ResourceName, JsonElement> _manifest;
    private readonly Func<ApplicationName, ResourceName, CancellationToken, ValueTask<ResourceInstanceInventory>> _inventory;
    private readonly Lock _catalogGate = new();
    private readonly Dictionary<(ApplicationName, ResourceName), (Guid Incarnation, Guid Observation, JsonElement Manifest)> _catalogs = new();

    public ResourceManagementCoordinator(IResourceManagementStore store, IResourceStateProtocolClient protocol,
        TimeProvider clock, Func<ApplicationName, ResourceName, JsonElement> manifest,
        Func<ApplicationName, ResourceName, CancellationToken, ValueTask<ResourceInstanceInventory>> inventory)
    {
        _store = store;
        _protocol = protocol;
        _clock = clock;
        _manifest = manifest;
        _inventory = inventory;
    }

    public JsonElement GetRuntimeManifest(ApplicationName application, ResourceName resource)
    {
        _ = _manifest(application, resource);
        lock (_catalogGate)
        {
            return _catalogs.TryGetValue((application, resource), out var discovered)
                ? discovered.Manifest.Clone() : throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable);
        }
    }

    private async ValueTask DiscoverAsync(ApplicationName application, ResourceName resource, ResourceInstanceInventory inventory, CancellationToken cancellationToken)
    {
        JsonElement binding = _manifest(application, resource);
        Guid priorObservation;
        lock (_catalogGate) { priorObservation = _catalogs.TryGetValue((application, resource), out var previous) ? previous.Observation : Guid.Empty; }
        Task<(ResourceManagementTarget Target, ResourceRuntimeDescription? Description)>[] requests = inventory.Instances
            .Where(static target => !target.IsTerminating).Select(async target =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            try { return (target, await _protocol.DescribeAsync(target, application, resource, budget.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or System.Net.Http.HttpRequestException or OperationCanceledException)
            {
                return (target, (ResourceRuntimeDescription?)null);
            }
        }).ToArray();
        var descriptions = new List<(ResourceManagementTarget Target, ResourceRuntimeDescription Description)>();
        foreach ((ResourceManagementTarget target, ResourceRuntimeDescription? description) in await Task.WhenAll(requests).ConfigureAwait(false))
        {
            if (description is null) { continue; }
            ValidateDiscoveredCatalog(binding, description.Manifest);
            if (description.ProcessStartId == Guid.Empty) { throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable); }
            descriptions.Add((target, description));
        }
        await AdmitConfiguredBaselinesAsync(application, resource, inventory, descriptions, cancellationToken).ConfigureAwait(false);
        JsonElement? catalog = null;
        string? digest = null;
        foreach ((_, ResourceRuntimeDescription description) in descriptions)
        {
            string candidateDigest = ComputeCatalogDigest(description.Manifest);
            if (digest is not null && candidateDigest != digest) { throw new InvalidOperationException(ErrorMessages.ManagementStateConflict); }
            catalog = description.Manifest.Clone();
            digest = candidateDigest;
        }
        ResourceInstanceInventory current = await _inventory(application, resource, cancellationToken).ConfigureAwait(false);
        if (current.Incarnation != inventory.Incarnation || current.Revision != inventory.Revision)
        {
            throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable);
        }
        lock (_catalogGate)
        {
            Guid observed = _catalogs.TryGetValue((application, resource), out var previous) ? previous.Observation : Guid.Empty;
            if (observed != priorObservation) { throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable); }
            if (catalog is JsonElement manifest) { _catalogs[(application, resource)] = (inventory.Incarnation, Guid.NewGuid(), manifest); }
            else if (observed == Guid.Empty || previous.Incarnation != inventory.Incarnation)
            {
                throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable);
            }
        }
    }

    private static void ValidateDiscoveredCatalog(JsonElement binding, JsonElement catalog)
    {
        if (!catalog.TryGetProperty("schema", out JsonElement schema) || schema.GetString() != "cohesion/resource-runtime/v1" ||
            !catalog.TryGetProperty("management", out JsonElement management) ||
            ResourceStateWire.ComputeDigest(management) != ResourceStateWire.ComputeDigest(binding.GetProperty("management")) ||
            !catalog.TryGetProperty("states", out JsonElement states) || states.ValueKind != JsonValueKind.Array)
        {
            throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
        }
        var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement definition in states.EnumerateArray())
        {
            if (!ResourceStateWire.IsValidDefinition(definition, requireDigest: true) || !definitions.TryAdd(definition.GetProperty("name").GetString()!, definition))
            {
                throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
            }
        }
        if (!catalog.TryGetProperty("initialState", out JsonElement initial)) { return; }
        var slots = new HashSet<(string, string)>();
        foreach (JsonElement declaration in initial.EnumerateArray())
        {
            string name = declaration.GetProperty("name").GetString()!;
            string key = declaration.GetProperty("key").GetString()!;
            JsonElement desired = declaration.GetProperty("desired");
            if (!slots.Add((name, key)) || !definitions.TryGetValue(name, out JsonElement definition) ||
                !definition.GetProperty("uses").EnumerateArray().Any(use => use.GetString() == "orchestration") ||
                !ResourceStateWire.ValidateValue(definition.GetProperty("keySchema"), declaration.GetProperty("key")) ||
                (!desired.GetProperty("exists").GetBoolean() && !definition.GetProperty("supportsAbsence").GetBoolean()) ||
                (desired.GetProperty("exists").GetBoolean() && !ResourceStateWire.ValidateValue(definition.GetProperty("payloadSchema"), desired.GetProperty("value"))))
            {
                throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
            }
        }
    }

    public async ValueTask<ResourceStateOperation> CommitAsync(ResourceStateChange change, string subject,
        bool baseline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (change.RequestId == Guid.Empty || change.Address.State.Value is null || change.Address.Key.Value is null ||
            change.Desired is null || change.Desired.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(ErrorMessages.ManagementStateInvalid, nameof(change));
        }
        ResourceStateAddress address = change.Address;
        ResourceInstanceInventory inventory = await _inventory(address.Application, address.Resource, cancellationToken).ConfigureAwait(false);
        if (!baseline) { await DiscoverAsync(address.Application, address.Resource, inventory, cancellationToken).ConfigureAwait(false); }
        JsonElement contract = GetContract(address, baseline ? "orchestration" : "management");
        ResourceDesiredValue desiredValue = change.ClearOverride ? ReadDeclaredBaseline(address) : change.Desired;
        string digest = ResourceStateWire.ComputeValueDigest(desiredValue.Exists, desiredValue.Value);
        using JsonDocument keyDocument = JsonDocument.Parse(JsonSerializer.Serialize(address.Key.Value, ResourceManagementJsonContext.Default.String));
        if ((change.ClearOverride && (baseline || change.ExpectedRevision is null)) ||
            (!desiredValue.Exists && !contract.GetProperty("supportsAbsence").GetBoolean()) ||
            !ResourceStateWire.ValidateValue(contract.GetProperty("keySchema"), keyDocument.RootElement) ||
            (desiredValue.Exists && !ResourceStateWire.ValidateValue(contract.GetProperty("payloadSchema"), desiredValue.Value)))
        {
            throw new ArgumentException(ErrorMessages.ManagementStateInvalid, nameof(change));
        }
        if (inventory.Incarnation == Guid.Empty) { throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable); }
        string requestKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject + "\0" + change.RequestId.ToString("D"))));
        string requestDigest = ResourceStateWire.ComputeDigest(JsonSerializer.SerializeToElement(change, ResourceManagementJsonContext.Default.ResourceStateChange));
        string slotKey = SlotKey(address);
        for (int attempt = 0; attempt < 32; attempt++)
        {
            (ResourceManagementState state, string? version) = await ReadAsync(address.Application, address.Resource, cancellationToken).ConfigureAwait(false);
            if (state.Incarnation != inventory.Incarnation)
            {
                state = new ResourceManagementState { Incarnation = inventory.Incarnation };
            }
            if (state.Requests.TryGetValue(requestKey, out ResourceManagementRequestReceipt? receipt))
            {
                if (receipt.Digest != requestDigest) { throw new InvalidOperationException(ErrorMessages.ManagementStateConflict); }
                return state.Operations[receipt.OperationId];
            }
            state.Slots.TryGetValue(slotKey, out ResourceManagementSlot? previous);
            if (baseline && previous is not null && previous.HasOverride)
            {
                if (previous.Baseline is not null && ResourceStateWire.ComputeValueDigest(previous.Baseline.Exists, previous.Baseline.Value) == digest)
                {
                    return state.Operations[previous.OperationId];
                }
                state.Slots[slotKey] = previous with { Baseline = Clone(change.Desired) };
                if (await SaveAsync(address, version, state, cancellationToken).ConfigureAwait(false)) { return state.Operations[previous.OperationId]; }
                continue;
            }
            if (baseline && previous?.ValueDigest == digest && previous.ContractDigest == contract.GetProperty("contractDigest").GetString())
            {
                return state.Operations[previous.OperationId];
            }
            if (!baseline && change.ExpectedRevision != previous?.Revision)
            {
                throw new InvalidOperationException(ErrorMessages.ManagementStateConflict);
            }
            if (previous is not null)
            {
                state.Operations[previous.OperationId] = state.Operations[previous.OperationId] with { Status = ResourceStateOperationStatus.Superseded };
            }
            long revision = checked(++state.NextRevision);
            var operation = new ResourceStateOperation
            {
                Id = Guid.NewGuid(), Address = address, Revision = revision, Desired = Clone(desiredValue),
                Scope = contract.GetProperty("scope").GetString()!, Status = ResourceStateOperationStatus.Accepted,
                ObservedAt = _clock.GetUtcNow()
            };
            state.Slots[slotKey] = new ResourceManagementSlot
            {
                Address = address, Desired = Clone(desiredValue), Baseline = baseline || change.ClearOverride ? Clone(desiredValue) : previous?.Baseline,
                HasOverride = !baseline && !change.ClearOverride, ContractDigest = contract.GetProperty("contractDigest").GetString()!,
                ValueDigest = digest, Revision = revision, OperationId = operation.Id, Fence = previous?.Fence ?? 0
            };
            state.Operations.Add(operation.Id, operation);
            state.Requests.Add(requestKey, new ResourceManagementRequestReceipt(requestDigest, operation.Id));
            if (await SaveAsync(address, version, state, cancellationToken).ConfigureAwait(false)) { return operation; }
        }
        throw new InvalidOperationException(ErrorMessages.ManagementStateConflict);
    }

    private ResourceDesiredValue ReadDeclaredBaseline(ResourceStateAddress address)
    {
        JsonElement runtime = GetRuntimeManifest(address.Application, address.Resource);
        if (runtime.TryGetProperty("initialState", out JsonElement declarations))
        {
            foreach (JsonElement declaration in declarations.EnumerateArray())
            {
                if (declaration.GetProperty("name").GetString() != address.State.Value || declaration.GetProperty("key").GetString() != address.Key.Value) { continue; }
                JsonElement desired = declaration.GetProperty("desired");
                return new ResourceDesiredValue(desired.GetProperty("exists").GetBoolean(), desired.GetProperty("value").Clone());
            }
        }
        throw new InvalidOperationException(ErrorMessages.ManagementBaselineRequired);
    }

    public async ValueTask<ResourceStateOperation?> GetAsync(ResourceStateAddress address, CancellationToken cancellationToken)
    {
        (ResourceManagementState state, _) = await ReadAsync(address.Application, address.Resource, cancellationToken).ConfigureAwait(false);
        return state.Slots.TryGetValue(SlotKey(address), out ResourceManagementSlot? slot) ? state.Operations[slot.OperationId] : null;
    }

    public async ValueTask<ResourceStateOperation?> GetOperationAsync(ApplicationName application, ResourceName resource, Guid id, CancellationToken cancellationToken)
    {
        (ResourceManagementState state, _) = await ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        return state.Operations.GetValueOrDefault(id);
    }

    public async ValueTask ReconcileAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken)
    {
        ResourceInstanceInventory inventory = await _inventory(application, resource, cancellationToken).ConfigureAwait(false);
        if (inventory.Incarnation == Guid.Empty) { return; }
        await DiscoverAsync(application, resource, inventory, cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < 32; attempt++)
        {
            (ResourceManagementState existing, string? version) = await ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
            if (existing.Incarnation == inventory.Incarnation) { break; }
            var initialized = new ResourceManagementState { Incarnation = inventory.Incarnation };
            if (await SaveAsync(new ResourceStateAddress(application, resource, "cohesion.bootstrap", "admission"), version, initialized, cancellationToken).ConfigureAwait(false)) { break; }
        }
        JsonElement runtime = GetRuntimeManifest(application, resource);
        if (runtime.TryGetProperty("initialState", out JsonElement initial))
        {
            foreach (JsonElement declaration in initial.EnumerateArray())
            {
                ResourceStateName name = declaration.GetProperty("name").GetString()!;
                ResourceStateKey key = declaration.GetProperty("key").GetString()!;
                JsonElement desired = declaration.GetProperty("desired");
                byte[] requestHash = SHA256.HashData(Encoding.UTF8.GetBytes(resource.Value + "\0" + declaration.GetRawText()));
                await CommitAsync(new ResourceStateChange(new Guid(requestHash.AsSpan(0, 16)), new ResourceStateAddress(application, resource, name, key), null,
                    new ResourceDesiredValue(desired.GetProperty("exists").GetBoolean(), desired.GetProperty("value").Clone())),
                    "orchestration", true, cancellationToken).ConfigureAwait(false);
            }
        }
        (ResourceManagementState state, _) = await ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        if (state.Incarnation != inventory.Incarnation) { return; }
        foreach (ResourceManagementSlot snapshot in state.Slots.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceManagementSlot? slot = await AcquireAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (slot is null) { continue; }
            JsonElement contract = GetContract(slot.Address, null);
            if (contract.GetProperty("contractDigest").GetString() != slot.ContractDigest)
            {
                await CheckpointAsync(slot, inventory, [], ResourceStateOperationStatus.Degraded, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var progress = new List<ResourceStateInstanceProgress>();
            ResourceManagementTarget[] targets = inventory.Instances.Where(static target => !target.IsTerminating).ToArray();
            bool shared = contract.GetProperty("scope").GetString() == "sharedState";
            foreach (ResourceManagementTarget target in targets)
            {
                slot = await AcquireAsync(slot, cancellationToken, advanceFence: shared && progress.Count != 0).ConfigureAwait(false);
                if (slot is null) { break; }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(15));
                Guid process = Guid.Empty;
                try
                {
                    ResourceRuntimeStateObservation observed = await _protocol.ReadAsync(target, slot.Address, slot.ContractDigest, budget.Token).ConfigureAwait(false);
                    process = observed.ProcessStartId;
                    ResourceStateDeliveryResult applied = observed.Revision == slot.Revision && observed.ValueDigest == slot.ValueDigest
                        ? new ResourceStateDeliveryResult(observed.Revision)
                        : await _protocol.ApplyAsync(target, new ResourceStateDelivery(slot.Address, inventory.Incarnation, process, slot.Revision,
                            slot.ValueDigest, slot.ContractDigest, slot.Fence, Clone(slot.Desired)), budget.Token).ConfigureAwait(false);
                    progress.Add(new ResourceStateInstanceProgress(target.HostId, target.Generation, process, applied.AppliedRevision, applied.ErrorCode));
                    if (shared && applied.AppliedRevision == slot.Revision && applied.ErrorCode is null) { break; }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException or ArgumentException)
                {
                    progress.Add(new ResourceStateInstanceProgress(target.HostId, target.Generation, process, null, "instance-unavailable"));
                }
            }
            if (slot is null) { continue; }
            ResourceInstanceInventory current = await _inventory(application, resource, cancellationToken).ConfigureAwait(false);
            if (current.Incarnation != inventory.Incarnation || current.Revision != inventory.Revision) { continue; }
            bool appliedAll = shared
                ? progress.Any(item => item.ProcessStartId != Guid.Empty && item.AppliedRevision == slot.Revision && item.ErrorCode is null)
                : progress.Count == targets.Length && progress.All(item => item.ProcessStartId != Guid.Empty && item.AppliedRevision == slot.Revision && item.ErrorCode is null);
            ResourceStateOperationStatus status = inventory.DesiredInstances == 0 && targets.Length == 0
                ? ResourceStateOperationStatus.Deferred
                : appliedAll && (shared || inventory.IsComplete && targets.Length >= inventory.DesiredInstances)
                    ? ResourceStateOperationStatus.Converged : ResourceStateOperationStatus.Degraded;
            await CheckpointAsync(slot, inventory, progress, status, cancellationToken).ConfigureAwait(false);
        }
        (ResourceManagementState latest, _) = await ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        if (latest.Incarnation != inventory.Incarnation) { return; }
        ResourceBootstrapState[] required = latest.Slots.Values.Select(slot => new ResourceBootstrapState(slot.Address.State, slot.Address.Key, slot.Revision, slot.ValueDigest)).ToArray();
        foreach (ResourceManagementTarget target in inventory.Instances.Where(static instance => !instance.IsTerminating))
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(15));
                ResourceRuntimeDescription description = await _protocol.DescribeAsync(target, application, resource, budget.Token).ConfigureAwait(false);
                await _protocol.AdmitAsync(target, application, resource, description.ProcessStartId, required, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
            {
                // Bootstrap remains closed on an unavailable or incompatible target; later passes retry.
            }
        }
    }

    private async ValueTask<ResourceManagementSlot?> AcquireAsync(ResourceManagementSlot snapshot, CancellationToken cancellationToken, bool advanceFence = false)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            (ResourceManagementState state, string? version) = await ReadAsync(snapshot.Address.Application, snapshot.Address.Resource, cancellationToken).ConfigureAwait(false);
            if (!state.Slots.TryGetValue(SlotKey(snapshot.Address), out ResourceManagementSlot? slot) || slot.Revision != snapshot.Revision) { return null; }
            DateTimeOffset now = _clock.GetUtcNow();
            if (slot.LeaseOwner != _owner && slot.LeaseExpiresAt > now) { return null; }
            slot = slot with { LeaseOwner = _owner, Fence = !advanceFence && slot.LeaseOwner == _owner && slot.LeaseExpiresAt > now ? slot.Fence : checked(slot.Fence + 1), LeaseExpiresAt = now.AddMinutes(1) };
            state.Slots[SlotKey(slot.Address)] = slot;
            if (await SaveAsync(slot.Address, version, state, cancellationToken).ConfigureAwait(false)) { return slot; }
        }
        return null;
    }

    private async ValueTask CheckpointAsync(ResourceManagementSlot slot, ResourceInstanceInventory inventory,
        IReadOnlyList<ResourceStateInstanceProgress> progress, ResourceStateOperationStatus status, CancellationToken cancellationToken)
    {
        ResourceInstanceInventory latest = await _inventory(slot.Address.Application, slot.Address.Resource, cancellationToken).ConfigureAwait(false);
        if (latest.Incarnation != inventory.Incarnation || latest.Revision != inventory.Revision) { return; }
        for (int attempt = 0; attempt < 32; attempt++)
        {
            (ResourceManagementState state, string? version) = await ReadAsync(slot.Address.Application, slot.Address.Resource, cancellationToken).ConfigureAwait(false);
            if (state.Incarnation != inventory.Incarnation || !state.Slots.TryGetValue(SlotKey(slot.Address), out ResourceManagementSlot? current) ||
                current.Revision != slot.Revision || current.LeaseOwner != _owner || current.Fence != slot.Fence || current.LeaseExpiresAt <= _clock.GetUtcNow()) { return; }
            state.Operations[slot.OperationId] = state.Operations[slot.OperationId] with
            {
                Status = status, InventoryRevision = inventory.Revision, DesiredInstances = inventory.DesiredInstances,
                InventoryComplete = inventory.IsComplete, Instances = progress.ToArray(), ObservedAt = _clock.GetUtcNow()
            };
            if (await SaveAsync(slot.Address, version, state, cancellationToken).ConfigureAwait(false)) { return; }
        }
    }

    private JsonElement GetContract(ResourceStateAddress address, string? use)
    {
        JsonElement manifest = GetRuntimeManifest(address.Application, address.Resource);
        if (manifest.GetProperty("schema").GetString() != "cohesion/resource-runtime/v1" ||
            manifest.GetProperty("management").GetProperty("protocol").GetString() != "cohesion/resource-state/v1")
        {
            throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
        }
        foreach (JsonElement definition in manifest.GetProperty("states").EnumerateArray())
        {
            if (definition.GetProperty("name").GetString() == address.State.Value &&
                (use is null || definition.GetProperty("uses").EnumerateArray().Any(item => item.GetString() == use)))
            {
                return definition;
            }
        }
        throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
    }

    private async ValueTask<(ResourceManagementState State, string? Version)> ReadAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken)
    {
        ResourceManagementDocument? document = await _store.ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        if (document is null) { return (new ResourceManagementState(), null); }
        ResourceManagementState state = JsonSerializer.Deserialize(document.Content.Span, ResourceManagementJsonContext.Default.ResourceManagementState)
            ?? throw new InvalidDataException(ErrorMessages.ManagementStateInvalid);
        if (state.SchemaVersion != 1) { throw new InvalidDataException(ErrorMessages.ManagementStateInvalid); }
        return (state, document.Version);
    }

    private ValueTask<bool> SaveAsync(ResourceStateAddress address, string? version, ResourceManagementState state, CancellationToken cancellationToken)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(state, ResourceManagementJsonContext.Default.ResourceManagementState);
        if (_store is not IProjectedResourceManagementStore projected)
        {
            return _store.TryWriteAsync(address.Application, address.Resource, version, content, cancellationToken);
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "cohesion/resource-state-projection/v1");
            writer.WriteString("incarnation", state.Incarnation);
            writer.WriteStartArray("entries");
            foreach (ResourceManagementSlot slot in state.Slots.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("name", slot.Address.State.Value);
                writer.WriteString("key", slot.Address.Key.Value);
                writer.WriteString("scope", state.Operations[slot.OperationId].Scope);
                writer.WriteString("contractDigest", slot.ContractDigest);
                writer.WriteString("valueDigest", slot.ValueDigest);
                writer.WriteNumber("revision", slot.Revision);
                writer.WriteNumber("fence", slot.Revision);
                writer.WritePropertyName("desired");
                JsonSerializer.Serialize(writer, slot.Desired, ResourceManagementJsonContext.Default.ResourceDesiredValue);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return projected.TryWriteProjectionAsync(address.Application, address.Resource, version, content, buffer.WrittenMemory.ToArray(), cancellationToken);
    }

    private static string SlotKey(ResourceStateAddress address) => address.State.Value + "\0" + address.Key.Value;
    private static ResourceDesiredValue Clone(ResourceDesiredValue value) => value with { Value = value.Value.Clone() };

    private sealed class DebugView
    {
        public DebugView(ResourceManagementCoordinator value) { }
    }
}
