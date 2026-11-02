using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The typed state of a stateful action: its current value, and how a new value is applied to the
/// instance.
/// </summary>
/// <typeparam name="TState">
/// The state's type. Use an immutable type, such as a record, so a value read while a write is
/// applied is never torn. The control plane's content serializer must know it.
/// </typeparam>
/// <remarks>
/// <para>
/// The initial value is the state the resource's code defines, already in effect when the state is
/// created, so creation applies nothing. Every later value passes through the apply callback, from
/// an action's write (<see cref="SetAsync"/>) or a restored snapshot (<see cref="RestoreAsync"/>),
/// and becomes current only when the callback succeeds. A snapshot therefore always describes a
/// value that was applied. A callback that refuses a value throws
/// <see cref="ApplicationContentException"/>, which a control plane reports as rejected.
/// </para>
/// <para>
/// The state does not serialize changes itself: its control plane runs one write or restore at a
/// time for the whole resource. Change it only from an action.
/// </para>
/// </remarks>
[DebuggerDisplay("{Value}")]
[DebuggerTypeProxy(typeof(ApplicationInstanceActionState<>.DebugView))]
public sealed class ApplicationInstanceActionState<TState> : IApplicationInstanceActionState
{
    private readonly Func<TState, CancellationToken, ValueTask> _apply;
    private TState _value;

    private ApplicationInstanceActionState(TState value, Func<TState, CancellationToken, ValueTask> apply)
    {
        _value = value;
        _apply = apply;
    }

    /// <summary>
    /// Gets the state's current value.
    /// </summary>
    public TState Value => _value;

    /// <summary>
    /// Applies a value to the instance and makes it the current value.
    /// </summary>
    /// <param name="value">The new value.</param>
    /// <param name="cancellationToken">Cancels the change before it is applied.</param>
    /// <returns>A task that completes when the value is in effect.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ApplicationContentException">The apply callback refused the value.</exception>
    public async ValueTask SetAsync(TState value, CancellationToken cancellationToken = default)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _apply.Invoke(value, cancellationToken).ConfigureAwait(false);
        _value = value;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> GetSnapshot(IApplicationContentSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        return serializer.Serialize(_value);
    }

    /// <inheritdoc/>
    /// <exception cref="ApplicationContentException">
    /// The snapshot is not a value of <typeparamref name="TState"/>, is a null value, or the apply
    /// callback refused it.
    /// </exception>
    public ValueTask RestoreAsync(ReadOnlyMemory<byte> snapshot, IApplicationContentSerializer serializer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        TState? value = serializer.Deserialize<TState>(snapshot);
        if (value is null)
        {
            throw new ApplicationContentException(ErrorMessages.ApplicationActionSnapshotNull);
        }

        return SetAsync(value, cancellationToken);
    }

    /// <summary>
    /// Creates the state of a stateful action whose new values are applied asynchronously.
    /// </summary>
    /// <param name="value">The initial value: the state the resource's code defines.</param>
    /// <param name="apply">Applies a new value to the instance.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static ApplicationInstanceActionState<TState> Create(TState value, Func<TState, CancellationToken, ValueTask> apply)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        ArgumentNullException.ThrowIfNull(apply);

        return new ApplicationInstanceActionState<TState>(value, apply);
    }

    /// <summary>
    /// Creates the state of a stateful action whose new values are applied synchronously.
    /// </summary>
    /// <param name="value">The initial value: the state the resource's code defines.</param>
    /// <param name="apply">Applies a new value to the instance.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static ApplicationInstanceActionState<TState> Create(TState value, Action<TState> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        return Create(value, (state, _) =>
        {
            apply.Invoke(state);
            return ValueTask.CompletedTask;
        });
    }

    private sealed class DebugView
    {
        private readonly ApplicationInstanceActionState<TState> _state;

        public DebugView(ApplicationInstanceActionState<TState> state)
        {
            _state = state;
        }

        public TState Value => _state.Value;
    }
}
