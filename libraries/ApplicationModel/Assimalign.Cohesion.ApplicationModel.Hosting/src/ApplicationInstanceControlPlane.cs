using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The control plane of one resource instance.
/// </summary>
/// <remarks>
/// <para>
/// The control plane accepts invocations and restores from the moment it is created. An
/// <see cref="ApplicationInstanceService"/> publishes its <see cref="Manifest"/> and restores the gateway's
/// state when the instance's host starts.
/// </para>
/// <para>
/// Reads run concurrently. Writes and restores run one at a time for the whole resource, so the
/// snapshot a write returns is the state that write left. The control plane reads the resource's
/// actions once, when it is created.
/// </para>
/// <para>
/// Content refused with <see cref="ApplicationContentException"/>, by the content serializer or by an
/// action, is reported as <see cref="ApplicationActionStatus.Rejected"/> and changes nothing.
/// </para>
/// </remarks>
[DebuggerDisplay("{Resource.Provider}/{Resource.Name} {Resource.Key}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ApplicationInstanceControlPlane : IApplicationInstanceControlPlane
{
    private readonly FrozenDictionary<ActionName, IApplicationInstanceAction> _actions;
    private readonly SemaphoreSlim _mutations = new(1, 1);

    private ApplicationInstanceControlPlane(
        IApplicationResourceInstance resource,
        IApplicationContentSerializer serializer,
        FrozenDictionary<ActionName, IApplicationInstanceAction> actions,
        ApplicationInstanceManifest manifest)
    {
        Resource = resource;
        Manifest = manifest;
        Serializer = serializer;
        _actions = actions;
    }

    /// <inheritdoc/>
    public IApplicationResourceInstance Resource { get; }

    /// <inheritdoc/>
    public ApplicationInstanceManifest Manifest { get; }

    /// <inheritdoc/>
    public IApplicationContentSerializer Serializer { get; }

    /// <summary>
    /// Creates the control plane of a resource instance.
    /// </summary>
    /// <param name="resource">The resource instance.</param>
    /// <param name="serializer">
    /// Writes and reads the instance's content. It must know the type of every action's state, which
    /// creation checks, and every action's input and output type.
    /// </param>
    /// <returns>The control plane.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The resource's provider, name, or key is <see langword="default"/>; it returns a
    /// <see langword="null"/> collection; one of its actions is <see langword="null"/>, has no name,
    /// shares a name with another, declares an operation other than Read or Write, or has state
    /// without accepting writes; or <paramref name="serializer"/> cannot write an action's state.
    /// </exception>
    public static ApplicationInstanceControlPlane Create(IApplicationResourceInstance resource, IApplicationContentSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(serializer);

        if (resource.Provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(resource));
        }

        if (resource.Name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(resource));
        }

        if (resource.Key == default)
        {
            throw new ArgumentException(ErrorMessages.InstanceKeyMissing, nameof(resource));
        }

        if (resource.HealthContributors is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceHealthContributorsMissing, nameof(resource));
        }

        IReadOnlyCollection<IApplicationInstanceAction> declared = resource.Actions
            ?? throw new ArgumentException(ErrorMessages.ResourceActionsMissing, nameof(resource));

        var actions = new Dictionary<ActionName, IApplicationInstanceAction>(declared.Count);
        var descriptors = new List<ApplicationActionDescriptor>(declared.Count);
        foreach (IApplicationInstanceAction action in declared)
        {
            if (action is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceActionNull, nameof(resource));
            }

            ActionName name = action.Name;
            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ActionNameMissing, nameof(resource));
            }

            if (action.Kind is not (ApplicationActionKind.Read or ApplicationActionKind.Write or ApplicationActionKind.ReadWrite))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationActionKindInvalid, name), nameof(resource));
            }

            IApplicationActionState? state = action.State;
            if (state is not null && !action.Kind.HasFlag(ApplicationActionKind.Write))
            {
                throw new ArgumentException(Format(ErrorMessages.ApplicationActionStateReadOnly, name), nameof(resource));
            }

            if (!actions.TryAdd(name, action))
            {
                throw new ArgumentException(Format(ErrorMessages.ActionNameDuplicate, name), nameof(resource));
            }

            // A serializer that cannot write a state would fail the first write after it changed the
            // instance, leaving state the gateway never sees: refuse it before anything runs.
            if (state is not null)
            {
                try
                {
                    _ = state.GetSnapshot(serializer);
                }
                catch (NotSupportedException exception)
                {
                    throw new ArgumentException(Format(ErrorMessages.ApplicationActionStateUnsupported, name), nameof(serializer), exception);
                }
            }

            descriptors.Add(new ApplicationActionDescriptor(name, action.Kind, state is not null));
        }

        descriptors.Sort(static (left, right) => left.Name.CompareTo(right.Name));

        // Where the gateway reaches the control plane is the platform's to say: the gateway client that
        // publishes the manifest names it.
        var manifest = new ApplicationInstanceManifest(resource.Provider, resource.Name, resource.Key, resource.Deployment, ControlPlaneEndpoint: null, descriptors.AsReadOnly());
        return new ApplicationInstanceControlPlane(resource, serializer, actions.ToFrozenDictionary(), manifest);
    }

    /// <inheritdoc/>
    public async ValueTask<ApplicationActionResult> ExecuteAsync(ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_actions.TryGetValue(action, out IApplicationInstanceAction? target))
        {
            return ApplicationActionResult.NotFound();
        }

        if ((target.Kind & context.Operation) == ApplicationActionKind.None)
        {
            return ApplicationActionResult.NotSupported();
        }

        if (context.Operation == ApplicationActionKind.Read)
        {
            return await InvokeAsync(target, context, cancellationToken).ConfigureAwait(false);
        }

        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationActionResult result = await InvokeAsync(target, context, cancellationToken).ConfigureAwait(false);
            if (result.Status == ApplicationActionStatus.Succeeded && target.State is { } state)
            {
                return result.WithState(ApplicationActionSnapshot.Create(target.Name, state.GetSnapshot(Serializer)));
            }

            return result;
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ApplicationActionResult> RestoreAsync(ApplicationActionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_actions.TryGetValue(snapshot.Action, out IApplicationInstanceAction? target))
        {
            return ApplicationActionResult.NotFound();
        }

        if (target.State is not { } state)
        {
            return ApplicationActionResult.NotSupported();
        }

        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await state.RestoreAsync(snapshot.Value, Serializer, cancellationToken).ConfigureAwait(false);
            return ApplicationActionResult.Success();
        }
        catch (ApplicationContentException exception)
        {
            // The snapshot does not describe a value the state accepts: the sender's error.
            return ApplicationActionResult.Rejected(exception.Message);
        }
        finally
        {
            _mutations.Release();
        }
    }

    private static async ValueTask<ApplicationActionResult> InvokeAsync(IApplicationInstanceAction action, ApplicationActionContext context, CancellationToken cancellationToken)
    {
        ApplicationActionResult? result;
        try
        {
            result = await action.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (ApplicationContentException exception)
        {
            // The input did not bind to the action's input type, or the action refused its value:
            // the caller's error, not the action's.
            return ApplicationActionResult.Rejected(exception.Message);
        }

        return result ?? throw new InvalidOperationException(Format(ErrorMessages.ApplicationActionResultNull, action.Name));
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ApplicationInstanceControlPlane _controlPlane;

        public DebugView(ApplicationInstanceControlPlane controlPlane)
        {
            _controlPlane = controlPlane;
        }

        public IApplicationResourceInstance Resource => _controlPlane.Resource;

        public ApplicationInstanceManifest Manifest => _controlPlane.Manifest;

        public IApplicationContentSerializer Serializer => _controlPlane.Serializer;
    }
}
