using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// A gateway's view of one application resource: its registered instances and the state recorded
/// since the gateway was deployed.
/// </summary>
/// <remarks>
/// Changes take one lock. Reads take none: <see cref="Instances"/> and <see cref="State"/> return
/// read-only copies replaced on each change.
/// </remarks>
[DebuggerDisplay("{Manifest.Provider}/{Manifest.Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationGatewayDeployment : IApplicationGatewayDeployment
{
    private readonly Lock _lock = new();
    private readonly SortedDictionary<InstanceKey, ApplicationInstanceManifest> _instances = new();
    private readonly SortedDictionary<ActionName, ApplicationActionSnapshot> _state;
    private readonly FrozenDictionary<ActionName, IApplicationGatewayDeploymentAction> _actions;
    private ReadOnlyCollection<ApplicationInstanceManifest> _instancesView = ReadOnlyCollection<ApplicationInstanceManifest>.Empty;
    private ReadOnlyCollection<ApplicationActionSnapshot> _stateView;
    private FrozenDictionary<OutputName, string> _outputs;

    private ApplicationGatewayDeployment(
        ApplicationResourceManifest manifest,
        SortedDictionary<ActionName, ApplicationActionSnapshot> state,
        SortedDictionary<ActionName, IApplicationGatewayDeploymentAction> actions,
        FrozenDictionary<OutputName, string> outputs)
    {
        Manifest = manifest;
        _state = state;
        _stateView = Copy(state.Values);
        _actions = actions.ToFrozenDictionary();
        _outputs = outputs;
        Actions = Copy(actions.Values);
    }

    /// <inheritdoc/>
    public ProviderName Provider => Manifest.Provider;

    /// <inheritdoc/>
    public ResourceName Name => Manifest.Name;

    /// <inheritdoc/>
    public ApplicationResourceManifest Manifest { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<ApplicationInstanceManifest> Instances => Volatile.Read(ref _instancesView);

    /// <inheritdoc/>
    public IReadOnlyCollection<ApplicationActionSnapshot> State => Volatile.Read(ref _stateView);

    /// <inheritdoc/>
    public IReadOnlyCollection<IApplicationGatewayDeploymentAction> Actions { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<OutputName, string> Outputs => Volatile.Read(ref _outputs);

    /// <summary>
    /// Creates the aggregate of a declared resource.
    /// </summary>
    /// <param name="manifest">The resource as the deployment declared it.</param>
    /// <param name="state">
    /// The state recorded earlier in the same deployment, which the gateway persisted before it
    /// restarted; <see langword="null"/> or empty for a new deployment.
    /// </param>
    /// <param name="actions">
    /// The infrastructure actions the gateway exposes on the resource; <see langword="null"/> or
    /// empty for none.
    /// </param>
    /// <param name="outputs">
    /// The outputs recorded earlier for the workload that still runs; <see langword="null"/> or empty
    /// for none yet.
    /// </param>
    /// <returns>The aggregate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The manifest's provider or name is <see langword="default"/>; <paramref name="state"/> holds a
    /// <see langword="null"/> snapshot or two snapshots for one action; <paramref name="actions"/>
    /// holds a <see langword="null"/> action, an action without a name or with an operation other
    /// than Read or Write, or two actions with one name; or <paramref name="outputs"/> holds an output
    /// without a name or a value.
    /// </exception>
    public static ApplicationGatewayDeployment Create(
        ApplicationResourceManifest manifest,
        IEnumerable<ApplicationActionSnapshot>? state = null,
        IEnumerable<IApplicationGatewayDeploymentAction>? actions = null,
        IReadOnlyDictionary<OutputName, string>? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(manifest));
        }

        if (manifest.Name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(manifest));
        }

        var snapshots = new SortedDictionary<ActionName, ApplicationActionSnapshot>();
        foreach (ApplicationActionSnapshot snapshot in state ?? [])
        {
            if (snapshot is null)
            {
                throw new ArgumentException(ErrorMessages.ApplicationResourceStateNull, nameof(state));
            }

            if (!snapshots.TryAdd(snapshot.Action, snapshot))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceStateDuplicate, snapshot.Action), nameof(state));
            }
        }

        var exposed = new SortedDictionary<ActionName, IApplicationGatewayDeploymentAction>();
        foreach (IApplicationGatewayDeploymentAction action in actions ?? [])
        {
            if (action is null)
            {
                throw new ArgumentException(ErrorMessages.ApplicationResourceActionNull, nameof(actions));
            }

            if (action.Name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ActionNameMissing, nameof(actions));
            }

            if (action.Kind is not (ApplicationActionKind.Read or ApplicationActionKind.Write or ApplicationActionKind.ReadWrite))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceActionKindInvalid, action.Name), nameof(actions));
            }

            if (!exposed.TryAdd(action.Name, action))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceActionDuplicate, manifest.Provider, manifest.Name, action.Name), nameof(actions));
            }
        }

        return new ApplicationGatewayDeployment(manifest, snapshots, exposed, CopyOutputs(manifest, outputs ?? FrozenDictionary<OutputName, string>.Empty, nameof(outputs)));
    }

    /// <summary>
    /// Replaces the resource's outputs with the values its platform reported.
    /// </summary>
    /// <param name="outputs">The outputs, by name.</param>
    /// <returns>Whether any output was added, removed, or changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outputs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputs"/> holds an output without a name or a value.</exception>
    public bool SetOutputs(IReadOnlyDictionary<OutputName, string> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        FrozenDictionary<OutputName, string> copy = CopyOutputs(Manifest, outputs, nameof(outputs));
        lock (_lock)
        {
            FrozenDictionary<OutputName, string> current = _outputs;
            bool changed = current.Count != copy.Count;
            foreach (KeyValuePair<OutputName, string> output in copy)
            {
                if (changed)
                {
                    break;
                }

                changed = !current.TryGetValue(output.Key, out string? value) || !string.Equals(value, output.Value, StringComparison.Ordinal);
            }

            if (changed)
            {
                Volatile.Write(ref _outputs, copy);
            }

            return changed;
        }
    }

    /// <summary>
    /// Removes the state recorded for the resource, as when a deployment redefines it.
    /// </summary>
    /// <remarks>
    /// Registered instances keep the state they restored. An instance that registers later restores
    /// none until a write records new state.
    /// </remarks>
    public void ClearState()
    {
        lock (_lock)
        {
            _state.Clear();
            Volatile.Write(ref _stateView, ReadOnlyCollection<ApplicationActionSnapshot>.Empty);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ApplicationActionResult> ExecuteAsync(ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_actions.TryGetValue(action, out IApplicationGatewayDeploymentAction? target))
        {
            return ApplicationActionResult.NotFound();
        }

        // Each operation has the value of the kind of the same name.
        if (!target.Kind.HasFlag((ApplicationActionKind)context.Operation))
        {
            return ApplicationActionResult.NotSupported();
        }

        ApplicationActionResult? result;
        try
        {
            result = await target.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (ApplicationContentException exception)
        {
            // The input did not bind to the action's input type, or the action refused its value:
            // the caller's error, not the action's.
            return ApplicationActionResult.Rejected(exception.Message);
        }

        return result ?? throw new InvalidOperationException(Format(ErrorMessages.ApplicationResourceActionResultNull, target.Name));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ApplicationActionSnapshot> Register(ApplicationInstanceManifest instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Provider != Manifest.Provider || instance.Name != Manifest.Name)
        {
            throw new ArgumentException(
                Format(ErrorMessages.ApplicationResourceMismatch, instance.Provider, instance.Name, Manifest.Provider, Manifest.Name),
                nameof(instance));
        }

        if (instance.Key == default)
        {
            throw new ArgumentException(ErrorMessages.InstanceKeyMissing, nameof(instance));
        }

        if (instance.Actions is null)
        {
            throw new ArgumentException(ErrorMessages.ApplicationInstanceManifestActionsMissing, nameof(instance));
        }

        lock (_lock)
        {
            _instances[instance.Key] = instance;
            Volatile.Write(ref _instancesView, Copy(_instances.Values));

            var restore = new List<ApplicationActionSnapshot>();
            foreach (ApplicationActionSnapshot snapshot in _state.Values)
            {
                if (IsStateful(instance, snapshot.Action))
                {
                    restore.Add(snapshot);
                }
            }

            return restore.AsReadOnly();
        }
    }

    /// <inheritdoc/>
    public bool Unregister(InstanceKey key)
    {
        lock (_lock)
        {
            if (!_instances.Remove(key))
            {
                return false;
            }

            Volatile.Write(ref _instancesView, Copy(_instances.Values));
            return true;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<InstanceKey> Record(InstanceKey source, ApplicationActionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            if (!_instances.TryGetValue(source, out ApplicationInstanceManifest? origin))
            {
                throw new InvalidOperationException(Format(ErrorMessages.ApplicationResourceInstanceUnknown, source, Manifest.Provider, Manifest.Name));
            }

            if (!IsStateful(origin, snapshot.Action))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceActionNotStateful, source, snapshot.Action), nameof(snapshot));
            }

            _state[snapshot.Action] = snapshot;
            Volatile.Write(ref _stateView, Copy(_state.Values));

            var targets = new List<InstanceKey>();
            foreach (KeyValuePair<InstanceKey, ApplicationInstanceManifest> instance in _instances)
            {
                if (instance.Key != source && IsStateful(instance.Value, snapshot.Action))
                {
                    targets.Add(instance.Key);
                }
            }

            return targets.AsReadOnly();
        }
    }

    // An instance of another version may lack the action, or declare it without state; it neither
    // receives nor reports that action's state.
    private static bool IsStateful(ApplicationInstanceManifest instance, ActionName action)
    {
        foreach (ApplicationActionDescriptor descriptor in instance.Actions)
        {
            if (descriptor.IsStateful && descriptor.Name == action)
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenDictionary<OutputName, string> CopyOutputs(ApplicationResourceManifest manifest, IReadOnlyDictionary<OutputName, string> outputs, string parameter)
    {
        foreach (KeyValuePair<OutputName, string> output in outputs)
        {
            if (output.Key.Value is null || output.Value is null)
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceOutputsNull, manifest.Provider, manifest.Name), parameter);
            }
        }

        return outputs.ToFrozenDictionary();
    }

    private static ReadOnlyCollection<T> Copy<T>(ICollection<T> values)
    {
        var copy = new T[values.Count];
        values.CopyTo(copy, 0);
        return Array.AsReadOnly(copy);
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ApplicationGatewayDeployment _resource;

        public DebugView(ApplicationGatewayDeployment resource)
        {
            _resource = resource;
        }

        public ApplicationResourceManifest Manifest => _resource.Manifest;

        public IReadOnlyCollection<ApplicationInstanceManifest> Instances => _resource.Instances;

        public IReadOnlyCollection<ApplicationActionSnapshot> State => _resource.State;

        public IReadOnlyCollection<IApplicationGatewayDeploymentAction> Actions => _resource.Actions;

        public IReadOnlyDictionary<OutputName, string> Outputs => _resource.Outputs;
    }
}
