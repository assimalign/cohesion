using System;
using System.Diagnostics;
using System.Text.Json;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// A string format defined by a predicate: a format the library does not define, or a replacement for
/// one it does. Pass it in <see cref="JsonSchemaEvaluationOptions.Formats"/>.
/// </summary>
/// <remarks>
/// Like the built-in formats, it constrains strings only: every other instance type satisfies it. A
/// format over another type implements <see cref="IJsonSchemaFormat"/> directly.
/// </remarks>
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaFormat : IJsonSchemaFormat
{
    private readonly Func<ReadOnlySpan<char>, bool> _isValid;

    private JsonSchemaFormat(string name, Func<ReadOnlySpan<char>, bool> isValid)
    {
        Name = name;
        _isValid = isValid;
    }

    /// <summary>
    /// Gets the format name, as written in the <c>format</c> keyword.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Creates a string format.
    /// </summary>
    /// <param name="name">The format name, as written in the <c>format</c> keyword.</param>
    /// <param name="isValid">
    /// Determines whether a string conforms. It receives the decoded string, which it must not keep.
    /// </param>
    /// <returns>The format.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="isValid"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static JsonSchemaFormat Create(string name, Func<ReadOnlySpan<char>, bool> isValid)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(isValid);
        return new JsonSchemaFormat(name, isValid);
    }

    /// <summary>
    /// Determines whether an instance satisfies the format.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <returns>
    /// <see langword="true"/> when the instance is a string the predicate accepts, or is not a string.
    /// </returns>
    public bool IsValid(JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        Span<char> buffer = stackalloc char[JsonText.BufferLength];
        return _isValid(JsonText.GetString(instance, buffer));
    }

    private sealed class DebugView
    {
        private readonly JsonSchemaFormat _format;

        public DebugView(JsonSchemaFormat format)
        {
            _format = format;
        }

        public string Name => _format.Name;
    }
}
