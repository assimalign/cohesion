using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The typed state of a stateful action: its current value, how a new value is applied to the
/// instance, and how the value is written to and read from a snapshot.
/// </summary>
/// <typeparam name="TState">
/// The state's type. Use an immutable type, such as a record, so a value read while a write is
/// applied is never torn.
/// </typeparam>
/// <remarks>
/// <para>
/// The initial value is the state the resource's code defines, already in effect when the state is
/// created, so creation applies nothing. Every later value passes through the apply callback, from
/// an action's write (<see cref="SetAsync"/>) or a restored snapshot (<see cref="RestoreAsync"/>),
/// and becomes current only when the callback succeeds. A snapshot therefore always describes a
/// value that was applied.
/// </para>
/// <para>
/// The state does not serialize changes itself: its control plane runs one write or restore at a
/// time for the whole resource. Change it only from an action.
/// </para>
/// </remarks>
[DebuggerDisplay("{Value}")]
[DebuggerTypeProxy(typeof(ResourceActionState<>.DebugView))]
public sealed class ResourceActionState<TState> : IResourceActionState
{
    private readonly JsonTypeInfo<TState> _typeInfo;
    private readonly Func<TState, CancellationToken, ValueTask> _apply;
    private TState _value;

    private ResourceActionState(TState value, JsonTypeInfo<TState> typeInfo, Func<TState, CancellationToken, ValueTask> apply)
    {
        _value = value;
        _typeInfo = typeInfo;
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
    public ReadOnlyMemory<byte> GetSnapshot()
    {
        return JsonSerializer.SerializeToUtf8Bytes(_value, _typeInfo);
    }

    /// <inheritdoc/>
    /// <exception cref="JsonException">The snapshot is not a value of <typeparamref name="TState"/>, or is the JSON literal <c>null</c>.</exception>
    public ValueTask RestoreAsync(ReadOnlyMemory<byte> snapshot, CancellationToken cancellationToken = default)
    {
        TState? value = JsonSerializer.Deserialize(snapshot.Span, _typeInfo);
        if (value is null)
        {
            throw new JsonException(ErrorMessages.ResourceStateSnapshotNull);
        }

        return SetAsync(value, cancellationToken);
    }

    /// <summary>
    /// Creates the state of a stateful action whose new values are applied asynchronously.
    /// </summary>
    /// <param name="value">The initial value: the state the resource's code defines.</param>
    /// <param name="typeInfo">The source-generated metadata of the state type.</param>
    /// <param name="apply">Applies a new value to the instance.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static ResourceActionState<TState> Create(TState value, JsonTypeInfo<TState> typeInfo, Func<TState, CancellationToken, ValueTask> apply)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(apply);

        return new ResourceActionState<TState>(value, typeInfo, apply);
    }

    /// <summary>
    /// Creates the state of a stateful action whose new values are applied synchronously.
    /// </summary>
    /// <param name="value">The initial value: the state the resource's code defines.</param>
    /// <param name="typeInfo">The source-generated metadata of the state type.</param>
    /// <param name="apply">Applies a new value to the instance.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static ResourceActionState<TState> Create(TState value, JsonTypeInfo<TState> typeInfo, Action<TState> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        return Create(value, typeInfo, (state, _) =>
        {
            apply.Invoke(state);
            return ValueTask.CompletedTask;
        });
    }

    private sealed class DebugView
    {
        private readonly ResourceActionState<TState> _state;

        public DebugView(ResourceActionState<TState> state)
        {
            _state = state;
        }

        public TState Value => _state.Value;
    }
}
