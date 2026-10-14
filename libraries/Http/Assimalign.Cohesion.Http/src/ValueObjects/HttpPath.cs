using System;
using System.Globalization;
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
    private static readonly SearchValues<char> _invalidCharacters = SearchValues.Create("?#\r\n\t ");

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

        if (value.AsSpan().ContainsAny(_invalidCharacters))
        {
            throw new HttpInvalidPathException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PathCharactersInvalid, value));
        }

        if (value[0] is not ('/' or '*'))
        {
            throw new HttpInvalidPathException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PathPrefixInvalid, value));
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
        if (Value is null)
        {
            return path;
        }

        if (path.Value is null || path.Value == "/")
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
    public bool StartsWith(HttpPath other) => other.Value is null || (Value?.StartsWith(other.Value, StringComparison.Ordinal) ?? false);

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

    /// <summary>
    /// Returns the path as a percent-encoded URI component, the inverse of <see cref="FromUriComponent(string)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each segment is encoded on its own (RFC 3986 §3.3): the <c>/</c> between segments and every
    /// <c>pchar</c> (unreserved, sub-delims, <c>:</c> and <c>@</c>) are written as they are, and every
    /// other character is percent-encoded as UTF-8 (§2.1). A lone surrogate is written as U+FFFD.
    /// </para>
    /// <para>
    /// A <c>%</c> is written as <c>%25</c>, except where it begins an escape that
    /// <see cref="FromUriComponent(string)"/> leaves as written: <c>%2F</c> or <c>%2f</c>, which it never
    /// decodes so that it never becomes a segment separator, and a sequence that is not valid UTF-8, such as
    /// the Latin-1 <c>%E9</c> or a truncated <c>%C3</c>. One in <see cref="Value"/> is the escape the request
    /// carried, and it is written as it is: encoding it again would produce <c>%252F</c> or <c>%25E9</c>, which
    /// decode to the same path but are different URIs. Any other <c>%</c> arrived as <c>%25</c>. So
    /// <c>FromUriComponent(path.ToUriComponent())</c> equals the path, and a redirect built from it names the
    /// resource the request named.
    /// </para>
    /// </remarks>
    /// <returns>The percent-encoded path; <see cref="Value"/> itself when nothing needs encoding.</returns>
    public string ToUriComponent() => UrlEncoder.EncodePath(Value ?? string.Empty);

    public override string ToString() => Value ?? string.Empty;
    public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
    public override bool Equals(object? obj) => obj is HttpPath path && Equals(path);
    public static implicit operator HttpPath(string value) => new(value);
    public static implicit operator string(HttpPath path) => path.Value ?? string.Empty;
}
