using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single parsed RFC 7239 <c>forwarded-element</c>: an ordered set of <c>forwarded-pair</c>s
/// (<c>token "=" value</c>) recorded by one proxy hop. The four registered parameters —
/// <c>for</c>, <c>by</c>, <c>host</c>, and <c>proto</c> — are surfaced through typed accessors;
/// any additional (extension) parameters are preserved in <see cref="Parameters"/> and round-trip
/// through <see cref="Serialize"/>.
/// </summary>
/// <remarks>
/// <para>
/// A value that contains characters outside the RFC 9110 <c>token</c> set (a port <c>:</c>, an
/// IPv6 <c>[</c>/<c>]</c>, a host with a port) is carried on the wire as a quoted-string; parsing
/// unescapes it and serialization re-quotes it as needed, so consumers always see the logical
/// value. The <c>for</c> and <c>by</c> values are additionally parsed into <see cref="HttpForwardedNode"/>s,
/// and an element whose <c>for</c>/<c>by</c> value is not a well-formed node is rejected.
/// </para>
/// <para>
/// This models one element; the comma-separated element <em>list</em> that forms a whole
/// <c>Forwarded</c> header is <see cref="HttpForwardedElementCollection"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpForwardedElement : IEquatable<HttpForwardedElement>
{
    private const string ForName = "for";
    private const string ByName = "by";
    private const string HostName = "host";
    private const string ProtoName = "proto";

    private readonly HttpForwardedParameter[]? parameters;
    private readonly HttpForwardedNode forNode;
    private readonly HttpForwardedNode byNode;

    private HttpForwardedElement(HttpForwardedParameter[] parameters, HttpForwardedNode forNode, HttpForwardedNode byNode)
    {
        this.parameters = parameters;
        this.forNode = forNode;
        this.byNode = byNode;
    }

    /// <summary>
    /// Gets the <c>for</c> node (the client or upstream proxy the recording proxy received the
    /// request from), or <see langword="null"/> when the element has no <c>for</c> parameter.
    /// </summary>
    public HttpForwardedNode? For => forNode.IsEmpty ? null : forNode;

    /// <summary>
    /// Gets the <c>by</c> node (the interface of the recording proxy the request came in on), or
    /// <see langword="null"/> when the element has no <c>by</c> parameter.
    /// </summary>
    public HttpForwardedNode? By => byNode.IsEmpty ? null : byNode;

    /// <summary>
    /// Gets the <c>host</c> value (the <c>Host</c> header as received by the proxy), or
    /// <see langword="null"/> when the element has no <c>host</c> parameter.
    /// </summary>
    public string? Host => TryGetParameter(HostName, out string? value) ? value : null;

    /// <summary>
    /// Gets the <c>proto</c> value (the protocol the request was made over, e.g. <c>https</c>), or
    /// <see langword="null"/> when the element has no <c>proto</c> parameter.
    /// </summary>
    public string? Proto => TryGetParameter(ProtoName, out string? value) ? value : null;

    /// <summary>
    /// Gets the element's parameters in wire order, including the registered
    /// <c>for</c>/<c>by</c>/<c>host</c>/<c>proto</c> pairs and any extension pairs.
    /// </summary>
    public IReadOnlyList<HttpForwardedParameter> Parameters
        => parameters ?? (IReadOnlyList<HttpForwardedParameter>)Array.Empty<HttpForwardedParameter>();

    /// <summary>Gets a value indicating whether this is the default (unparsed) instance.</summary>
    public bool IsEmpty => parameters is null;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Creates a forwarded element from typed values, emitting parameters in the canonical order
    /// <c>for</c>, <c>by</c>, <c>host</c>, <c>proto</c>, then any extensions.
    /// </summary>
    /// <param name="for">The <c>for</c> node, or <see langword="null"/> to omit it.</param>
    /// <param name="by">The <c>by</c> node, or <see langword="null"/> to omit it.</param>
    /// <param name="host">The <c>host</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="proto">The <c>proto</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="extensions">Additional extension parameters, or <see langword="null"/>.</param>
    /// <returns>The constructed element.</returns>
    /// <exception cref="ArgumentException">
    /// A provided <paramref name="for"/>/<paramref name="by"/> node is empty, or
    /// <paramref name="host"/>/<paramref name="proto"/> is empty or whitespace, or an extension
    /// parameter reuses a registered name.
    /// </exception>
    public static HttpForwardedElement Create(
        HttpForwardedNode? @for = null,
        HttpForwardedNode? by = null,
        string? host = null,
        string? proto = null,
        IReadOnlyList<HttpForwardedParameter>? extensions = null)
    {
        var list = new List<HttpForwardedParameter>(4);
        HttpForwardedNode forValue = default;
        HttpForwardedNode byValue = default;

        if (@for is { } forNode)
        {
            if (forNode.IsEmpty)
            {
                throw new ArgumentException("The 'for' node is empty.", nameof(@for));
            }
            forValue = forNode;
            list.Add(new HttpForwardedParameter(ForName, forNode.ToString()));
        }
        if (by is { } byNode)
        {
            if (byNode.IsEmpty)
            {
                throw new ArgumentException("The 'by' node is empty.", nameof(by));
            }
            byValue = byNode;
            list.Add(new HttpForwardedParameter(ByName, byNode.ToString()));
        }
        if (host is not null)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("The 'host' value is empty.", nameof(host));
            }
            list.Add(new HttpForwardedParameter(HostName, host));
        }
        if (proto is not null)
        {
            if (string.IsNullOrWhiteSpace(proto))
            {
                throw new ArgumentException("The 'proto' value is empty.", nameof(proto));
            }
            list.Add(new HttpForwardedParameter(ProtoName, proto));
        }
        if (extensions is not null)
        {
            foreach (HttpForwardedParameter extension in extensions)
            {
                if (IsRegisteredName(extension.Name))
                {
                    throw new ArgumentException(
                        $"Extension parameter '{extension.Name}' reuses a registered parameter name.", nameof(extensions));
                }
                list.Add(extension);
            }
        }

        return new HttpForwardedElement(list.ToArray(), forValue, byValue);
    }

    /// <summary>
    /// Attempts to look up a parameter value by name (case-insensitive). The first occurrence wins.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the parameter value.</param>
    /// <returns><see langword="true"/> when a parameter with the given name is present.</returns>
    public bool TryGetParameter(string name, out string? value)
    {
        if (parameters is not null)
        {
            foreach (HttpForwardedParameter parameter in parameters)
            {
                if (parameter.HasName(name))
                {
                    value = parameter.Value;
                    return true;
                }
            }
        }
        value = null;
        return false;
    }

    /// <summary>
    /// Parses <paramref name="value"/> as a single RFC 7239 forwarded-element.
    /// </summary>
    /// <param name="value">The element text (e.g. <c>for=192.0.2.43;proto=https</c>).</param>
    /// <returns>The parsed element.</returns>
    /// <exception cref="HttpException">The value is not a well-formed forwarded-element.</exception>
    public static HttpForwardedElement Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpForwardedElement result))
        {
            throw new HttpInvalidForwardedException($"The value is not a valid forwarded element: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a single RFC 7239 forwarded-element.
    /// </summary>
    /// <param name="value">The element text, or <see langword="null"/>.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed element.</param>
    /// <returns><see langword="true"/> when the value is a well-formed, non-empty element.</returns>
    public static bool TryParse(string? value, out HttpForwardedElement result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a single RFC 7239 forwarded-element. Empty
    /// <c>;</c>-separated segments are ignored (RFC 7239 &#167; 4 permits them); a segment that is
    /// present but malformed — a missing <c>=</c>, a non-token name, an empty or non-token/non-quoted
    /// value, or a <c>for</c>/<c>by</c> value that is not a valid node — fails the whole parse. Never
    /// throws.
    /// </summary>
    /// <param name="value">The element text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed element.</param>
    /// <returns><see langword="true"/> when the value is a well-formed, non-empty element.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpForwardedElement result)
    {
        result = default;

        value = HttpFieldSyntax.TrimOws(value);
        if (value.IsEmpty)
        {
            return false;
        }

        List<HttpForwardedParameter>? list = null;
        HttpForwardedNode forNode = default;
        HttpForwardedNode byNode = default;

        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            int semicolon = HttpFieldSyntax.IndexOfUnquoted(remaining, ';');
            ReadOnlySpan<char> segment = HttpFieldSyntax.TrimOws(semicolon < 0 ? remaining : remaining[..semicolon]);
            remaining = semicolon < 0 ? ReadOnlySpan<char>.Empty : remaining[(semicolon + 1)..];

            if (segment.IsEmpty)
            {
                // An empty forwarded-pair (e.g. a leading, trailing, or doubled ';') is permitted.
                continue;
            }

            int equals = HttpFieldSyntax.IndexOfUnquoted(segment, '=');
            if (equals <= 0)
            {
                return false;
            }

            ReadOnlySpan<char> nameSpan = HttpFieldSyntax.TrimOws(segment[..equals]);
            ReadOnlySpan<char> valueSpan = HttpFieldSyntax.TrimOws(segment[(equals + 1)..]);
            if (!HttpFieldSyntax.IsToken(nameSpan))
            {
                return false;
            }

            if (!TryReadValue(valueSpan, out string parameterValue))
            {
                return false;
            }

            string name = ToLowerString(nameSpan);
            if (name.Equals(ForName, StringComparison.Ordinal))
            {
                if (!HttpForwardedNode.TryParse(parameterValue, out forNode))
                {
                    return false;
                }
            }
            else if (name.Equals(ByName, StringComparison.Ordinal))
            {
                if (!HttpForwardedNode.TryParse(parameterValue, out byNode))
                {
                    return false;
                }
            }

            (list ??= new List<HttpForwardedParameter>()).Add(new HttpForwardedParameter(name, parameterValue));
        }

        if (list is null)
        {
            // The element consisted solely of empty pairs — nothing to record.
            return false;
        }

        result = new HttpForwardedElement(list.ToArray(), forNode, byNode);
        return true;
    }

    private static bool TryReadValue(ReadOnlySpan<char> valueSpan, out string value)
    {
        value = string.Empty;
        if (valueSpan.IsEmpty)
        {
            return false;
        }

        if (valueSpan[0] == '"')
        {
            if (!HttpFieldSyntax.IsQuotedString(valueSpan))
            {
                return false;
            }
            value = HttpFieldSyntax.UnquoteValue(valueSpan);
            return true;
        }

        if (!HttpFieldSyntax.IsToken(valueSpan))
        {
            return false;
        }

        value = valueSpan.ToString();
        return true;
    }

    private static bool IsRegisteredName(string name)
        => name.Equals(ForName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(ByName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HostName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(ProtoName, StringComparison.OrdinalIgnoreCase);

    private static string ToLowerString(ReadOnlySpan<char> span)
    {
        Span<char> buffer = span.Length <= 64 ? stackalloc char[span.Length] : new char[span.Length];
        int written = span.ToLowerInvariant(buffer);
        return new string(buffer[..written]);
    }

    /// <summary>
    /// Serializes the element to its RFC 7239 wire form, quoting any value that is not a bare token.
    /// </summary>
    /// <returns>The wire form (e.g. <c>for=192.0.2.43;proto=https</c>), or an empty string for the default instance.</returns>
    public string Serialize()
    {
        if (parameters is null || parameters.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }
            builder.Append(parameters[i].Name).Append('=');
            AppendValue(builder, parameters[i].Value);
        }
        return builder.ToString();
    }

    private static void AppendValue(StringBuilder builder, string value)
    {
        if (value.Length > 0 && HttpFieldSyntax.IsToken(value.AsSpan()))
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        builder.Append('"');
    }

    /// <inheritdoc cref="Serialize" />
    public override string ToString() => Serialize();

    /// <inheritdoc />
    public bool Equals(HttpForwardedElement other)
    {
        int count = parameters?.Length ?? 0;
        if (count != (other.parameters?.Length ?? 0))
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!parameters![i].Equals(other.parameters![i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpForwardedElement other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (parameters is null)
        {
            return 0;
        }
        var hash = new HashCode();
        foreach (HttpForwardedParameter parameter in parameters)
        {
            hash.Add(parameter);
        }
        return hash.ToHashCode();
    }

    /// <summary>Determines whether two elements are equal.</summary>
    public static bool operator ==(HttpForwardedElement left, HttpForwardedElement right) => left.Equals(right);

    /// <summary>Determines whether two elements are not equal.</summary>
    public static bool operator !=(HttpForwardedElement left, HttpForwardedElement right) => !left.Equals(right);
}
