using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP method token.
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpMethod : IEquatable<HttpMethod>
{
    private const int MaximumLength = 32;
    private static readonly SearchValues<char> AllowedCharacters = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Initializes a new HTTP method.
    /// </summary>
    /// <param name="value">The method token.</param>
    public HttpMethod(string? value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(value);

        if (value.Length > MaximumLength)
        {
            throw new ArgumentException($"The method is too long. It must be {MaximumLength} characters or fewer.", nameof(value));
        }

        if (value.AsSpan().ContainsAnyExcept(AllowedCharacters))
        {
            throw new HttpInvalidMethodException($"The provided method is invalid: '{value}'.");
        }

        Value = value.ToUpperInvariant();
    }

    /// <summary>
    /// Gets the raw HTTP method token.
    /// </summary>
    public string Value { get; }

    public static HttpMethod Connect { get; } = new("CONNECT");
    public static HttpMethod Delete { get; } = new("DELETE");
    public static HttpMethod Get { get; } = new("GET");
    public static HttpMethod Head { get; } = new("HEAD");
    public static HttpMethod Options { get; } = new("OPTIONS");
    public static HttpMethod Patch { get; } = new("PATCH");
    public static HttpMethod Post { get; } = new("POST");
    public static HttpMethod Put { get; } = new("PUT");
    public static HttpMethod Trace { get; } = new("TRACE");

    /// <inheritdoc />
    public bool Equals(HttpMethod other)
    {
        return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a canonicalized method when one of the standard methods matches.
    /// </summary>
    /// <param name="method">The method to canonicalize.</param>
    /// <returns>A canonicalized method value.</returns>
    public static HttpMethod GetCanonicalizedValue(string method) => method.ToUpperInvariant() switch
    {
        "GET" => Get,
        "POST" => Post,
        "PUT" => Put,
        "DELETE" => Delete,
        "OPTIONS" => Options,
        "HEAD" => Head,
        "PATCH" => Patch,
        "TRACE" => Trace,
        "CONNECT" => Connect,
        _ => new HttpMethod(method),
    };

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is HttpMethod method && Equals(method);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public static implicit operator HttpMethod(string method) => new(method);
    public static implicit operator string(HttpMethod method) => method.Value;
    public static bool operator ==(HttpMethod left, HttpMethod right) => left.Equals(right);
    public static bool operator !=(HttpMethod left, HttpMethod right) => !left.Equals(right);
}
