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
/// with each optional member present only when set (<see cref="ApplicationActionResultJsonConverter"/>).
/// </para>
/// </remarks>
[JsonConverter(typeof(ApplicationActionResultJsonConverter))]
public sealed class ApplicationActionResult
{
    private static readonly ApplicationActionResult _succeeded = new(ApplicationActionStatus.Succeeded, default, null, null);
    private static readonly ApplicationActionResult _notFound = new(ApplicationActionStatus.NotFound, default, null, null);
    private static readonly ApplicationActionResult _notSupported = new(ApplicationActionStatus.NotSupported, default, null, null);

    private ApplicationActionResult(ApplicationActionStatus status, ReadOnlyMemory<byte> output, string? reason, ApplicationActionSnapshot? state)
    {
        Status = status;
        Output = output;
        Reason = reason;
        State = state;
    }

    /// <summary>
    /// Gets the outcome.
    /// </summary>
    public ApplicationActionStatus Status { get; }

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
    public ApplicationActionSnapshot? State { get; }

    /// <summary>
    /// Creates the result of a dispatcher that has no action with the requested name.
    /// </summary>
    /// <returns>The result.</returns>
    public static ApplicationActionResult NotFound()
    {
        return _notFound;
    }

    /// <summary>
    /// Creates the result of a dispatcher whose action does not accept the requested operation, or
    /// has no state to restore.
    /// </summary>
    /// <returns>The result.</returns>
    public static ApplicationActionResult NotSupported()
    {
        return _notSupported;
    }

    /// <summary>
    /// Creates a successful result with no output.
    /// </summary>
    /// <returns>The result.</returns>
    public static ApplicationActionResult Success()
    {
        return _succeeded;
    }

    /// <summary>
    /// Creates a successful result with an output.
    /// </summary>
    /// <param name="output">The output, written by the control plane's content serializer.</param>
    /// <returns>The result.</returns>
    public static ApplicationActionResult Success(ReadOnlyMemory<byte> output)
    {
        return output.IsEmpty ? _succeeded : new ApplicationActionResult(ApplicationActionStatus.Succeeded, output, null, null);
    }

    /// <summary>
    /// Creates a successful result whose output is a serialized value.
    /// </summary>
    /// <typeparam name="TOutput">The output type.</typeparam>
    /// <param name="output">The output value.</param>
    /// <param name="serializer">The invocation's content serializer (<see cref="ApplicationActionContext.Serializer"/>).</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="serializer"/> does not know <typeparamref name="TOutput"/>.</exception>
    public static ApplicationActionResult Success<TOutput>(TOutput output, IApplicationContentSerializer serializer)
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
    public static ApplicationActionResult Rejected(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new ApplicationActionResult(ApplicationActionStatus.Rejected, default, reason, null);
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
    public ApplicationActionResult WithState(ApplicationActionSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new ApplicationActionResult(Status, Output, Reason, state);
    }

    // Rebuilds a result a transport received; the converter has validated the parts.
    internal static ApplicationActionResult Create(ApplicationActionStatus status, ReadOnlyMemory<byte> output, string? reason, ApplicationActionSnapshot? state)
    {
        return new ApplicationActionResult(status, output, reason, state);
    }
}
