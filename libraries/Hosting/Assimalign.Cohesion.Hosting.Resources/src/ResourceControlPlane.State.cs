using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

public sealed partial class ResourceControlPlane
{
    private readonly Dictionary<ResourceStateName, IResourceStateHandler> _stateHandlers = new();
    private ResourceRuntimeManifest? _runtimeManifest;
    private bool _stateHandlersFrozen;
    private int _managementReady = 1;

    /// <inheritdoc />
    public bool IsManagementReady => Volatile.Read(ref _managementReady) != 0;

    /// <inheritdoc />
    public void RequireStateBootstrap() => Volatile.Write(ref _managementReady, 0);

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The admission request is null.</exception>
    public async ValueTask<bool> AdmitStateAsync(ResourceStateAdmission admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.ProcessStartId != ProcessStartId || admission.States is null) { return false; }
        var admittedSlots = new HashSet<(ResourceStateName, ResourceStateKey)>();
        foreach (ResourceStateAdmissionSlot slot in admission.States)
        {
            if (slot is null || slot.Name.Value is null || slot.Key.Value is null || slot.Revision < 0 ||
                string.IsNullOrWhiteSpace(slot.ValueDigest) || !admittedSlots.Add((slot.Name, slot.Key))) { return false; }
        }
        foreach (ResourceStateDeclaration required in _runtimeManifest?.InitialState ?? Array.Empty<ResourceStateDeclaration>())
        {
            if (!admission.States.Any(slot => slot.Name == required.Name && slot.Key == required.Key)) { return false; }
        }
        foreach (ResourceStateAdmissionSlot slot in admission.States)
        {
            ResourceStateObservation observed = await ReadStateAsync(slot.Name, slot.Key, cancellationToken).ConfigureAwait(false);
            if (slot.Revision == 0)
            {
                ResourceStateDeclaration? configured = _runtimeManifest?.InitialState.FirstOrDefault(
                    declaration => declaration.Name == slot.Name && declaration.Key == slot.Key);
                if (configured is null || observed.Revision != 0 ||
                    slot.ValueDigest != ResourceStateWire.ComputeValueDigest(configured.Desired.Exists, configured.Desired.Value) ||
                    slot.ValueDigest != ResourceStateWire.ComputeValueDigest(observed.Value.Exists, observed.Value.Value)) { return false; }
            }
            else if (observed.Revision != slot.Revision || observed.ValueDigest != slot.ValueDigest) { return false; }
        }
        Volatile.Write(ref _managementReady, 1);
        return true;
    }

    /// <inheritdoc />
    public Guid ProcessStartId { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public ResourceRuntimeManagement? RuntimeManagement
    {
        get { lock (_sync) { return _runtimeManifest?.Management; } }
    }

    /// <inheritdoc />
    public ResourceRuntimeManifest? RuntimeManifest
    {
        get
        {
            lock (_sync)
            {
                return _runtimeManifest is null ? null : ResourceRuntimeManifest.Parse(Encoding.UTF8.GetString(_runtimeManifest.Export()));
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The management binding is null.</exception>
    /// <exception cref="System.IO.InvalidDataException">The management binding is invalid.</exception>
    /// <exception cref="InvalidOperationException">The host is attached or handlers were already registered.</exception>
    public void ConfigureRuntimeManagement(ResourceRuntimeManagement management)
    {
        ArgumentNullException.ThrowIfNull(management);
        var manifest = new ResourceRuntimeManifest { Management = management };
        ResourceRuntimeManifest snapshot = ResourceRuntimeManifest.Parse(Encoding.UTF8.GetString(manifest.Export()));
        lock (_sync)
        {
            if (_host is not null || _stateHandlersFrozen || _stateHandlers.Count != 0) { throw new InvalidOperationException(ErrorMessages.ResourceStateCatalogFrozen); }
            _runtimeManifest = snapshot;
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The handler is null.</exception>
    /// <exception cref="InvalidOperationException">The catalog is frozen or the handler metadata is missing, duplicated or incompatible.</exception>
    /// <exception cref="System.IO.InvalidDataException">The handler's contract or initial state is invalid.</exception>
    public void RegisterStateHandler(IResourceStateHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            if (_host is not null || _stateHandlersFrozen) { throw new InvalidOperationException(ErrorMessages.ResourceStateCatalogFrozen); }
            ResourceStateDefinition definition = handler.Definition
                ?? throw new InvalidOperationException(ErrorMessages.ResourceStateCatalogMismatch);
            ResourceStateDeclaration[] initial = (handler.InitialState
                ?? throw new InvalidOperationException(ErrorMessages.ResourceStateCatalogMismatch)).ToArray();
            if (_runtimeManifest is null) { throw new InvalidOperationException(ErrorMessages.ResourceStateCatalogMismatch); }
            if (_stateHandlers.ContainsKey(definition.Name) || initial.Any(state => state is null || state.Name != definition.Name))
            { throw new InvalidOperationException(ErrorMessages.ResourceStateCatalogMismatch); }
            ResourceRuntimeManifest discovered = _runtimeManifest with
            {
                States = _runtimeManifest.States.Append(definition).ToArray(),
                InitialState = _runtimeManifest.InitialState.Concat(initial).ToArray()
            };
            ResourceRuntimeManifest snapshot = ResourceRuntimeManifest.Parse(Encoding.UTF8.GetString(discovered.Export()));
            _stateHandlers.Add(definition.Name, handler);
            _runtimeManifest = snapshot;
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The advertised catalog and handlers differ.</exception>
    public void ValidateStateHandlers()
    {
        lock (_sync)
        {
            if ((_runtimeManifest?.States.Count ?? 0) != _stateHandlers.Count) { throw new InvalidOperationException(ErrorMessages.ResourceStateCatalogMismatch); }
            _stateHandlersFrozen = true;
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A state name or key is default.</exception>
    /// <exception cref="ArgumentNullException">The desired value is null.</exception>
    /// <exception cref="NotSupportedException">The state is not implemented.</exception>
    public async ValueTask<ResourceStateValidation> ValidateStateAsync(ResourceStateName name, ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default)
    {
        IResourceStateHandler handler = GetStateHandler(name, key);
        ArgumentNullException.ThrowIfNull(desired);
        cancellationToken.ThrowIfCancellationRequested();
        ResourceStateDefinition definition = _runtimeManifest!.States.First(state => state.Name == name);
        using JsonDocument keyDocument = JsonDocument.Parse(JsonSerializer.Serialize(key.Value, ResourceStateJsonContext.Default.String));
        if ((!desired.Exists && !definition.SupportsAbsence) || desired.Value.ValueKind == JsonValueKind.Undefined ||
            !ResourceStateWire.ValidateValue(definition.KeySchema, keyDocument.RootElement) ||
            (desired.Exists && !ResourceStateWire.ValidateValue(definition.PayloadSchema, desired.Value)))
        {
            return new ResourceStateValidation(false, "invalid-state-value");
        }
        return await handler.ValidateAsync(key, desired with { Value = desired.Value.Clone() }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A state name or key is default.</exception>
    /// <exception cref="NotSupportedException">The state is not implemented.</exception>
    public async ValueTask<ResourceStateObservation> ReadStateAsync(ResourceStateName name, ResourceStateKey key, CancellationToken cancellationToken = default)
    {
        IResourceStateHandler handler = GetStateHandler(name, key);
        ResourceStateDefinition definition = _runtimeManifest!.States.First(state => state.Name == name);
        if (!ResourceStateWire.ValidateValue(definition.KeySchema, JsonSerializer.SerializeToElement(key.Value, ResourceStateJsonContext.Default.String)))
        {
            throw new ArgumentException(ErrorMessages.ResourceStateKeyInvalid, nameof(key));
        }
        ResourceStateObservation observation = await handler.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        return observation with { ProcessStartId = ProcessStartId, Value = observation.Value with { Value = observation.Value.Value.Clone() } };
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The context or desired value is null.</exception>
    /// <exception cref="ArgumentException">A state name or key is default.</exception>
    /// <exception cref="NotSupportedException">The state is not implemented.</exception>
    public async ValueTask<ResourceStateApplyResult> ApplyStateAsync(ResourceStateApplyContext context, ResourceStateName name, ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(desired);
        IResourceStateHandler handler = GetStateHandler(name, key);
        if (context.TargetProcess != ProcessStartId || context.ResourceIncarnation == Guid.Empty || context.Revision <= 0 || context.Fence <= 0 ||
            desired.Value.ValueKind == JsonValueKind.Undefined || context.ValueDigest != ResourceStateWire.ComputeValueDigest(desired.Exists, desired.Value))
        {
            return new ResourceStateApplyResult(ResourceStateApplyStatus.Rejected, 0, "invalid-state-target");
        }
        ResourceStateValidation validation = await ValidateStateAsync(name, key, desired, cancellationToken).ConfigureAwait(false);
        return validation.IsValid
            ? await handler.ApplyAsync(context, key, desired with { Value = desired.Value.Clone() }, cancellationToken).ConfigureAwait(false)
            : new ResourceStateApplyResult(ResourceStateApplyStatus.Rejected, 0, validation.ErrorCode);
    }

    private IResourceStateHandler GetStateHandler(ResourceStateName name, ResourceStateKey key)
    {
        if (name.Value is null) { throw new ArgumentException(ErrorMessages.ResourceStateNameInvalid, nameof(name)); }
        if (key.Value is null) { throw new ArgumentException(ErrorMessages.ResourceStateKeyInvalid, nameof(key)); }
        lock (_sync)
        {
            return _stateHandlers.TryGetValue(name, out IResourceStateHandler? handler)
                ? handler : throw new NotSupportedException(ErrorMessages.ResourceStateUnsupported);
        }
    }
}
