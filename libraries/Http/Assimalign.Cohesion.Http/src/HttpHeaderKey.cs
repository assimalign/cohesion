using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP header name.
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpHeaderKey : IEquatable<HttpHeaderKey>, IComparable<HttpHeaderKey>
{
    /// <summary>
    /// Initializes a new header key.
    /// </summary>
    /// <param name="value">The header name.</param>
    public HttpHeaderKey(string value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>
    /// Gets the raw header name.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets a value indicating whether the header key is empty.
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <inheritdoc />
    public bool Equals(HttpHeaderKey other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    /// <inheritdoc />
    public int CompareTo(HttpHeaderKey other) => StringComparer.OrdinalIgnoreCase.Compare(Value, other.Value);

    public override string ToString() => Value;
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override bool Equals(object? instance) => instance is HttpHeaderKey key && Equals(key);

    public static implicit operator HttpHeaderKey(string key) => new(key);
    public static implicit operator string(HttpHeaderKey key) => key.Value;
    public static bool operator ==(HttpHeaderKey left, HttpHeaderKey right) => left.Equals(right);
    public static bool operator !=(HttpHeaderKey left, HttpHeaderKey right) => !left.Equals(right);

    public static HttpHeaderKey Accept { get; } = new("Accept");
    public static HttpHeaderKey AcceptEncoding { get; } = new("Accept-Encoding");
    public static HttpHeaderKey Authorization { get; } = new("Authorization");
    public static HttpHeaderKey Connection { get; } = new("Connection");
    public static HttpHeaderKey ContentLength { get; } = new("Content-Length");
    public static HttpHeaderKey ContentType { get; } = new("Content-Type");
    public static HttpHeaderKey Cookie { get; } = new("Cookie");
    public static HttpHeaderKey Date { get; } = new("Date");
    public static HttpHeaderKey Host { get; } = new("Host");
    public static HttpHeaderKey Location { get; } = new("Location");
    public static HttpHeaderKey Server { get; } = new("Server");
    public static HttpHeaderKey SetCookie { get; } = new("Set-Cookie");
    public static HttpHeaderKey TransferEncoding { get; } = new("Transfer-Encoding");
    public static HttpHeaderKey Upgrade { get; } = new("Upgrade");
    public static HttpHeaderKey UserAgent { get; } = new("User-Agent");
}
