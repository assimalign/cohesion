using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

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
public sealed class ApplicationResource : IApplicationResource
{
    private readonly Lock _lock = new();
    private readonly SortedDictionary<ResourceInstanceKey, ResourceManifest> _instances = new();
    private readonly SortedDictionary<ResourceActionName, ResourceStateSnapshot> _state;
    private readonly FrozenDictionary<ResourceActionName, IApplicationResourceAction> _actions;
    private ReadOnlyCollection<ResourceManifest> _instancesView = ReadOnlyCollection<ResourceManifest>.Empty;
    private ReadOnlyCollection<ResourceStateSnapshot> _stateView;
    private FrozenDictionary<OutputName, string> _outputs;

    private ApplicationResource(
        ApplicationResourceManifest manifest,
        SortedDictionary<ResourceActionName, ResourceStateSnapshot> state,
        SortedDictionary<ResourceActionName, IApplicationResourceAction> actions,
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
    public ApplicationResourceManifest Manifest { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<ResourceManifest> Instances => Volatile.Read(ref _instancesView);

    /// <inheritdoc/>
    public IReadOnlyCollection<ResourceStateSnapshot> State => Volatile.Read(ref _stateView);

    /// <inheritdoc/>
    public IReadOnlyCollection<IApplicationResourceAction> Actions { get; }

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
    public static ApplicationResource Create(
        ApplicationResourceManifest manifest,
        IEnumerable<ResourceStateSnapshot>? state = null,
        IEnumerable<IApplicationResourceAction>? actions = null,
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

        var snapshots = new SortedDictionary<ResourceActionName, ResourceStateSnapshot>();
        foreach (ResourceStateSnapshot snapshot in state ?? [])
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

        var exposed = new SortedDictionary<ResourceActionName, IApplicationResourceAction>();
        foreach (IApplicationResourceAction action in actions ?? [])
        {
            if (action is null)
            {
                throw new ArgumentException(ErrorMessages.ApplicationResourceActionNull, nameof(actions));
            }

            if (action.Name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceActionNameMissing, nameof(actions));
            }

            if (action.Kind is not (ResourceActionKind.Read or ResourceActionKind.Write or ResourceActionKind.ReadWrite))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceActionKindInvalid, action.Name), nameof(actions));
            }

            if (!exposed.TryAdd(action.Name, action))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceActionDuplicate, manifest.Provider, manifest.Name, action.Name), nameof(actions));
            }
        }

        return new ApplicationResource(manifest, snapshots, exposed, CopyOutputs(manifest, outputs ?? FrozenDictionary<OutputName, string>.Empty, nameof(outputs)));
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

    /// <inheritdoc/>
    public async ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionName action, ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_actions.TryGetValue(action, out IApplicationResourceAction? target))
        {
            return ResourceActionResult.NotFound();
        }

        if ((target.Kind & context.Operation) == ResourceActionKind.None)
        {
            return ResourceActionResult.NotSupported();
        }

        ResourceActionResult? result;
        try
        {
            result = await target.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceContentException exception)
        {
            // The input did not bind to the action's input type, or the action refused its value:
            // the caller's error, not the action's.
            return ResourceActionResult.Rejected(exception.Message);
        }

        return result ?? throw new InvalidOperationException(Format(ErrorMessages.ApplicationResourceActionResultNull, target.Name));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ResourceStateSnapshot> Register(ResourceManifest instance)
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
            throw new ArgumentException(ErrorMessages.ResourceInstanceKeyMissing, nameof(instance));
        }

        if (instance.Actions is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceManifestActionsMissing, nameof(instance));
        }

        lock (_lock)
        {
            _instances[instance.Key] = instance;
            Volatile.Write(ref _instancesView, Copy(_instances.Values));

            var restore = new List<ResourceStateSnapshot>();
            foreach (ResourceStateSnapshot snapshot in _state.Values)
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
    public bool Unregister(ResourceInstanceKey key)
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
    public IReadOnlyList<ResourceInstanceKey> Record(ResourceInstanceKey source, ResourceStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            if (!_instances.TryGetValue(source, out ResourceManifest? origin))
            {
                throw new InvalidOperationException(Format(ErrorMessages.ApplicationResourceInstanceUnknown, source, Manifest.Provider, Manifest.Name));
            }

            if (!IsStateful(origin, snapshot.Action))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationResourceActionNotStateful, source, snapshot.Action), nameof(snapshot));
            }

            _state[snapshot.Action] = snapshot;
            Volatile.Write(ref _stateView, Copy(_state.Values));

            var targets = new List<ResourceInstanceKey>();
            foreach (KeyValuePair<ResourceInstanceKey, ResourceManifest> instance in _instances)
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
    private static bool IsStateful(ResourceManifest instance, ResourceActionName action)
    {
        foreach (ResourceActionDescriptor descriptor in instance.Actions)
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
        private readonly ApplicationResource _resource;

        public DebugView(ApplicationResource resource)
        {
            _resource = resource;
        }

        public ApplicationResourceManifest Manifest => _resource.Manifest;

        public IReadOnlyCollection<ResourceManifest> Instances => _resource.Instances;

        public IReadOnlyCollection<ResourceStateSnapshot> State => _resource.State;

        public IReadOnlyCollection<IApplicationResourceAction> Actions => _resource.Actions;

        public IReadOnlyDictionary<OutputName, string> Outputs => _resource.Outputs;
    }
}
