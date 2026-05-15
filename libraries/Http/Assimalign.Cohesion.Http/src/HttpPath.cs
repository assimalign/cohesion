using System;
using System.Buffers;
using System.Diagnostics;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP request path.
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpPath : IEquatable<HttpPath>
{
    private static readonly SearchValues<char> InvalidCharacters = SearchValues.Create("?#\r\n\t ");

    /// <summary>
    /// Gets the canonical root path.
    /// </summary>
    public static HttpPath Root { get; } = new("/");

    /// <summary>
    /// Initializes a new path.
    /// </summary>
    /// <param name="value">The raw path value.</param>
    public HttpPath(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Value = "/";
            return;
        }

        if (value.AsSpan().ContainsAny(InvalidCharacters))
        {
            throw new HttpInvalidPathException($"The following path contains invalid characters: '{value}'.");
        }

        if (value[0] is not ('/' or '*'))
        {
            throw new HttpInvalidPathException($"The following path is invalid: '{value}'. Paths must begin with '/' or '*'.");
        }

        Value = value;
    }

    /// <summary>
    /// Gets the raw path value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Concatenates two paths.
    /// </summary>
    /// <param name="path">The path to append.</param>
    /// <returns>The combined path.</returns>
    public HttpPath Concat(HttpPath path)
    {
        if (path.Value == "/")
        {
            return this;
        }

        if (Value == "/")
        {
            return path;
        }

        string combined = $"{Value.TrimEnd('/')}/{path.Value.TrimStart('/')}";
        return new HttpPath(combined);
    }

    /// <inheritdoc />
    public bool Equals(HttpPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether the current path starts with the supplied path.
    /// </summary>
    /// <param name="other">The path to compare.</param>
    /// <returns><see langword="true"/> when the current path starts with the supplied path; otherwise <see langword="false"/>.</returns>
    public bool StartsWith(HttpPath other) => Value.StartsWith(other.Value, StringComparison.Ordinal);

    /// <summary>
    /// Creates a path from a percent-encoded URI component.
    /// </summary>
    /// <param name="uriComponent">The encoded URI component.</param>
    /// <returns>A decoded <see cref="HttpPath"/>.</returns>
    public static HttpPath FromUriComponent(string uriComponent)
    {
        ArgumentNullException.ThrowIfNull(uriComponent);

        int index = uriComponent.IndexOf('%');
        if (index < 0)
        {
            return new HttpPath(uriComponent);
        }

        Span<char> buffer = uriComponent.Length <= 256 ? stackalloc char[uriComponent.Length] : new char[uriComponent.Length];
        uriComponent.CopyTo(buffer);
        int decodedLength = UrlDecoder.DecodeInPlace(buffer[index..]);
        return new HttpPath(buffer[..(index + decodedLength)].ToString());
    }

    public override string ToString() => Value;
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override bool Equals(object? obj) => obj is HttpPath path && Equals(path);
    public static implicit operator HttpPath(string value) => new(value);
    public static implicit operator string(HttpPath path) => path.Value;
}
