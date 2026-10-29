using System;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The result of executing an action or restoring its state.
/// </summary>
/// <remarks>
/// <para>
/// An action returns <see cref="Success()"/> or <see cref="Rejected(string)"/>. A dispatcher, such
/// as a control plane, reports <see cref="NotFound()"/> and <see cref="NotSupported()"/> itself, and
/// a control plane adds <see cref="State"/> after a successful write to a stateful action. An action
/// that fails rather than refuses throws, and the exception reaches the transport.
/// </para>
/// <para>
/// A transport sends a result with the control plane's content serializer. In JSON it reads
/// <c>{ "status": "Succeeded", "output": …, "reason": "…", "state": { "action": "…", "value": … } }</c>,
/// with each optional member present only when set (<see cref="ResourceActionResultJsonConverter"/>).
/// </para>
/// </remarks>
[JsonConverter(typeof(ResourceActionResultJsonConverter))]
public sealed class ResourceActionResult
{
    private static readonly ResourceActionResult _succeeded = new(ResourceActionStatus.Succeeded, default, null, null);
    private static readonly ResourceActionResult _notFound = new(ResourceActionStatus.NotFound, default, null, null);
    private static readonly ResourceActionResult _notSupported = new(ResourceActionStatus.NotSupported, default, null, null);

    private ResourceActionResult(ResourceActionStatus status, ReadOnlyMemory<byte> output, string? reason, ResourceStateSnapshot? state)
    {
        Status = status;
        Output = output;
        Reason = reason;
        State = state;
    }

    /// <summary>
    /// Gets the outcome.
    /// </summary>
    public ResourceActionStatus Status { get; }

    /// <summary>
    /// Gets the action's output as content written by the control plane's content serializer; empty
    /// when it has none.
    /// </summary>
    public ReadOnlyMemory<byte> Output { get; }

    /// <summary>
    /// Gets why the request was rejected, or <see langword="null"/> when it was not.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Gets the snapshot of the state a successful write to a stateful action left, or
    /// <see langword="null"/> for any other result.
    /// </summary>
    public ResourceStateSnapshot? State { get; }

    /// <summary>
    /// Creates the result of a dispatcher that has no action with the requested name.
    /// </summary>
    /// <returns>The result.</returns>
    public static ResourceActionResult NotFound()
    {
        return _notFound;
    }

    /// <summary>
    /// Creates the result of a dispatcher whose action does not accept the requested operation, or
    /// has no state to restore.
    /// </summary>
    /// <returns>The result.</returns>
    public static ResourceActionResult NotSupported()
    {
        return _notSupported;
    }

    /// <summary>
    /// Creates a successful result with no output.
    /// </summary>
    /// <returns>The result.</returns>
    public static ResourceActionResult Success()
    {
        return _succeeded;
    }

    /// <summary>
    /// Creates a successful result with an output.
    /// </summary>
    /// <param name="output">The output, written by the control plane's content serializer.</param>
    /// <returns>The result.</returns>
    public static ResourceActionResult Success(ReadOnlyMemory<byte> output)
    {
        return output.IsEmpty ? _succeeded : new ResourceActionResult(ResourceActionStatus.Succeeded, output, null, null);
    }

    /// <summary>
    /// Creates a successful result whose output is a serialized value.
    /// </summary>
    /// <typeparam name="TOutput">The output type.</typeparam>
    /// <param name="output">The output value.</param>
    /// <param name="serializer">The invocation's content serializer (<see cref="ResourceActionContext.Serializer"/>).</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="serializer"/> does not know <typeparamref name="TOutput"/>.</exception>
    public static ResourceActionResult Success<TOutput>(TOutput output, IResourceContentSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        return Success(serializer.Serialize(output));
    }

    /// <summary>
    /// Creates a result that refuses the request.
    /// </summary>
    /// <param name="reason">Why the request was refused, for the operator who made it.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static ResourceActionResult Rejected(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new ResourceActionResult(ResourceActionStatus.Rejected, default, reason, null);
    }

    /// <summary>
    /// Returns this result with the snapshot a stateful write left, which the instance's gateway
    /// records and restores on the resource's other instances.
    /// </summary>
    /// <remarks>
    /// A control plane calls it after a write to a stateful action succeeds; the action itself
    /// returns its result without the snapshot.
    /// </remarks>
    /// <param name="state">The snapshot of the action's state after the write.</param>
    /// <returns>A result with the same status, output, and reason, carrying <paramref name="state"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    public ResourceActionResult WithState(ResourceStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new ResourceActionResult(Status, Output, Reason, state);
    }

    // Rebuilds a result a transport received; the converter has validated the parts.
    internal static ResourceActionResult Create(ResourceActionStatus status, ReadOnlyMemory<byte> output, string? reason, ResourceStateSnapshot? state)
    {
        return new ResourceActionResult(status, output, reason, state);
    }
}
