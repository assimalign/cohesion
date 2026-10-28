using System;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The state of one stateful action, written by the control plane's content serializer: what a
/// gateway keeps after a write and restores on instances that start before the resource is next
/// deployed.
/// </summary>
[JsonConverter(typeof(ResourceStateSnapshotJsonConverter))]
public sealed class ResourceStateSnapshot
{
    private ResourceStateSnapshot(ResourceActionName action, ReadOnlyMemory<byte> value)
    {
        Action = action;
        Value = value;
    }

    /// <summary>
    /// Gets the name of the action whose state this is.
    /// </summary>
    public ResourceActionName Action { get; }

    /// <summary>
    /// Gets the state's value as content.
    /// </summary>
    public ReadOnlyMemory<byte> Value { get; }

    /// <summary>
    /// Creates a snapshot.
    /// </summary>
    /// <remarks>The snapshot keeps a copy of <paramref name="value"/>, so the caller may reuse its buffer.</remarks>
    /// <param name="action">The name of the action whose state this is.</param>
    /// <param name="value">The state's value, written by the control plane's content serializer.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="action"/> is <see langword="default"/>, or <paramref name="value"/> is empty.
    /// </exception>
    public static ResourceStateSnapshot Create(ResourceActionName action, ReadOnlyMemory<byte> value)
    {
        if (action.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceActionNameMissing, nameof(action));
        }

        if (value.IsEmpty)
        {
            throw new ArgumentException(ErrorMessages.ResourceStateSnapshotEmpty, nameof(value));
        }

        return new ResourceStateSnapshot(action, value.ToArray());
    }
}
