using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One invocation of a resource action: the operation it requests, its input, and the content
/// serializer the input was written with.
/// </summary>
public sealed class ApplicationActionContext
{
    private ApplicationActionContext(ApplicationActionOperation operation, IApplicationContentSerializer serializer, ReadOnlyMemory<byte> input)
    {
        Operation = operation;
        Serializer = serializer;
        Input = input;
    }

    /// <summary>
    /// Gets the requested operation.
    /// </summary>
    public ApplicationActionOperation Operation { get; }

    /// <summary>
    /// Gets the content serializer of the control plane that dispatches the invocation. An action
    /// reads its input with it and writes its output with it
    /// (<see cref="ApplicationActionResult.Success{TOutput}(TOutput, IApplicationContentSerializer)"/>).
    /// </summary>
    public IApplicationContentSerializer Serializer { get; }

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
    /// <exception cref="ApplicationContentException">
    /// The input is empty or is not a value of <typeparamref name="TInput"/>. A control plane reports
    /// it as <see cref="ApplicationActionStatus.Rejected"/>.
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
    /// The content serializer of the control plane that dispatches the invocation.
    /// </param>
    /// <param name="input">The input written by <paramref name="serializer"/>, or empty for none.</param>
    /// <returns>The invocation context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> is not <see cref="ApplicationActionOperation.Read"/> or
    /// <see cref="ApplicationActionOperation.Write"/>.
    /// </exception>
    public static ApplicationActionContext Create(ApplicationActionOperation operation, IApplicationContentSerializer serializer, ReadOnlyMemory<byte> input = default)
    {
        if (operation is not (ApplicationActionOperation.Read or ApplicationActionOperation.Write))
        {
            throw new ArgumentException(ErrorMessages.ApplicationActionOperationInvalid, nameof(operation));
        }

        ArgumentNullException.ThrowIfNull(serializer);

        return new ApplicationActionContext(operation, serializer, input);
    }
}
