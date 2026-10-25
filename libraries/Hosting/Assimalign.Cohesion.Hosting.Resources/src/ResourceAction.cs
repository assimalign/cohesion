using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Declares an imperative operation a resource exposes, such as backing up now or flushing a cache.
/// </summary>
/// <remarks>
/// An action has no desired state: Cohesion stores nothing about it. Its effects live in the resource's
/// own data, where they can be observed. The gateway forwards an invocation to one instance or to every
/// instance, as <see cref="Target"/> declares.
/// </remarks>
[DebuggerDisplay("{Name,nq} ({Target})")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ResourceAction
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> _invoke;

    private ResourceAction(
        ResourceActionName name,
        ResourceActionTarget target,
        JsonElement inputSchema,
        JsonElement resultSchema,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> invoke)
    {
        Name = name;
        Target = target;
        InputSchema = inputSchema;
        ResultSchema = resultSchema;
        _invoke = invoke;
    }

    /// <summary>Gets the action name.</summary>
    public ResourceActionName Name { get; }

    /// <summary>Gets which instances run the action.</summary>
    public ResourceActionTarget Target { get; }

    /// <summary>Gets the JSON schema of the action's input.</summary>
    public JsonElement InputSchema { get; }

    /// <summary>Gets the JSON schema of the action's result.</summary>
    public JsonElement ResultSchema { get; }

    /// <summary>Creates an action over source-generated input and result metadata.</summary>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="name">The action name.</param>
    /// <param name="input">The input's source-generated metadata.</param>
    /// <param name="result">The result's source-generated metadata.</param>
    /// <param name="target">Which instances run the action.</param>
    /// <param name="invoke">Runs the action.</param>
    /// <returns>The action.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is the default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="input"/>, <paramref name="result"/> or <paramref name="invoke"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is undefined.</exception>
    public static ResourceAction Create<TInput, TResult>(
        ResourceActionName name,
        JsonTypeInfo<TInput> input,
        JsonTypeInfo<TResult> result,
        ResourceActionTarget target,
        Func<TInput, CancellationToken, ValueTask<TResult>> invoke)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceActionNameInvalid, nameof(name));
        }

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(invoke);
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        return new ResourceAction(name, target, ResourceSurfaceSchema.Create(input), ResourceSurfaceSchema.Create(result), async (body, cancellationToken) =>
        {
            TInput? value;
            try
            {
                value = JsonSerializer.Deserialize(body.Span, input);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(ErrorMessages.ResourceActionInputInvalid, nameof(body), exception);
            }

            if (value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceActionInputInvalid, nameof(body));
            }

            TResult output = await invoke(value, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(output, result);
        });
    }

    /// <summary>Binds the UTF-8 JSON input, runs the action, and returns its UTF-8 JSON result.</summary>
    internal ValueTask<byte[]> InvokeAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken) =>
        _invoke(input, cancellationToken);

    private sealed class DebugView
    {
        private readonly ResourceAction _target;

        public DebugView(ResourceAction value)
        {
            _target = value;
        }

        public ResourceActionName Name => _target.Name;

        public ResourceActionTarget Target => _target.Target;
    }
}
