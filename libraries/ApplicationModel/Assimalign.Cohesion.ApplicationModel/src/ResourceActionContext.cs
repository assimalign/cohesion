using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One invocation of a resource action: the operation it requests, its input, and the content
/// serializer the input was written with.
/// </summary>
public sealed class ResourceActionContext
{
    private ResourceActionContext(ResourceActionKind operation, IResourceContentSerializer serializer, ReadOnlyMemory<byte> input)
    {
        Operation = operation;
        Serializer = serializer;
        Input = input;
    }

    /// <summary>
    /// Gets the requested operation: exactly <see cref="ResourceActionKind.Read"/> or
    /// <see cref="ResourceActionKind.Write"/>.
    /// </summary>
    public ResourceActionKind Operation { get; }

    /// <summary>
    /// Gets the content serializer of the control plane that dispatches the invocation. An action
    /// reads its input with it and writes its output with it
    /// (<see cref="ResourceActionResult.Success{TOutput}(TOutput, IResourceContentSerializer)"/>).
    /// </summary>
    public IResourceContentSerializer Serializer { get; }

    /// <summary>
    /// Gets the invocation's input as content; empty when the invocation has none.
    /// </summary>
    public ReadOnlyMemory<byte> Input { get; }

    /// <summary>
    /// Reads the input as a value of the specified type.
    /// </summary>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <returns>The input; <see langword="null"/> when it is the format's null value.</returns>
    /// <exception cref="NotSupportedException"><see cref="Serializer"/> does not know <typeparamref name="TInput"/>.</exception>
    /// <exception cref="ResourceContentException">
    /// The input is empty or is not a value of <typeparamref name="TInput"/>. A control plane reports
    /// it as <see cref="ResourceActionStatus.Rejected"/>.
    /// </exception>
    public TInput? ReadInput<TInput>()
    {
        return Serializer.Deserialize<TInput>(Input);
    }

    /// <summary>
    /// Creates an invocation context.
    /// </summary>
    /// <param name="operation">The requested operation.</param>
    /// <param name="serializer">
    /// The content serializer of the control plane that dispatches the invocation
    /// (<see cref="IResourceControlPlane.Serializer"/>).
    /// </param>
    /// <param name="input">The input written by <paramref name="serializer"/>, or empty for none.</param>
    /// <returns>The invocation context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> is not exactly <see cref="ResourceActionKind.Read"/> or
    /// <see cref="ResourceActionKind.Write"/>.
    /// </exception>
    public static ResourceActionContext Create(ResourceActionKind operation, IResourceContentSerializer serializer, ReadOnlyMemory<byte> input = default)
    {
        if (operation is not (ResourceActionKind.Read or ResourceActionKind.Write))
        {
            throw new ArgumentException(ErrorMessages.ResourceActionOperationInvalid, nameof(operation));
        }

        ArgumentNullException.ThrowIfNull(serializer);

        return new ResourceActionContext(operation, serializer, input);
    }
}
