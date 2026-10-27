using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// One invocation of a resource action: the operation it requests and its UTF-8 JSON input.
/// </summary>
public sealed class ResourceActionContext
{
    private ResourceActionContext(ResourceActionKind operation, ReadOnlyMemory<byte> input)
    {
        Operation = operation;
        Input = input;
    }

    /// <summary>
    /// Gets the requested operation: exactly <see cref="ResourceActionKind.Read"/> or
    /// <see cref="ResourceActionKind.Write"/>.
    /// </summary>
    public ResourceActionKind Operation { get; }

    /// <summary>
    /// Gets the invocation's input as UTF-8 JSON; empty when the invocation has none.
    /// </summary>
    public ReadOnlyMemory<byte> Input { get; }

    /// <summary>
    /// Reads the input as a value of the specified type.
    /// </summary>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <param name="typeInfo">The source-generated metadata of the input type.</param>
    /// <returns>The input; <see langword="null"/> when it is the JSON literal <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">
    /// The input is empty or is not a value of <typeparamref name="TInput"/>. A control plane reports
    /// it as <see cref="ResourceActionStatus.Rejected"/>.
    /// </exception>
    public TInput? ReadInput<TInput>(JsonTypeInfo<TInput> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return JsonSerializer.Deserialize(Input.Span, typeInfo);
    }

    /// <summary>
    /// Creates an invocation context.
    /// </summary>
    /// <param name="operation">The requested operation.</param>
    /// <param name="input">The input as UTF-8 JSON, or empty for none.</param>
    /// <returns>The invocation context.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> is not exactly <see cref="ResourceActionKind.Read"/> or
    /// <see cref="ResourceActionKind.Write"/>.
    /// </exception>
    public static ResourceActionContext Create(ResourceActionKind operation, ReadOnlyMemory<byte> input = default)
    {
        if (operation is not (ResourceActionKind.Read or ResourceActionKind.Write))
        {
            throw new ArgumentException(ErrorMessages.ResourceActionOperationInvalid, nameof(operation));
        }

        return new ResourceActionContext(operation, input);
    }
}
