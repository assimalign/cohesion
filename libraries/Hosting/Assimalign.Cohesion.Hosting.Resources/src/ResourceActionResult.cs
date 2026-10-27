using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The result of executing an action or restoring its state.
/// </summary>
/// <remarks>
/// An action returns <see cref="Success()"/> or <see cref="Rejected(string)"/>. A dispatcher, such
/// as a control plane, reports <see cref="NotFound()"/> and <see cref="NotSupported()"/> itself, and
/// a control plane adds <see cref="State"/> after a successful write to a stateful action. An action
/// that fails rather than refuses throws, and the exception reaches the transport.
/// </remarks>
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
    /// Gets the action's output as UTF-8 JSON; empty when it has none.
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
    /// <param name="output">The output as UTF-8 JSON.</param>
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
    /// <param name="typeInfo">The source-generated metadata of the output type.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    public static ResourceActionResult Success<TOutput>(TOutput output, JsonTypeInfo<TOutput> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return new ResourceActionResult(ResourceActionStatus.Succeeded, JsonSerializer.SerializeToUtf8Bytes(output, typeInfo), null, null);
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

    internal ResourceActionResult WithState(ResourceStateSnapshot state)
    {
        return new ResourceActionResult(Status, Output, Reason, state);
    }
}
