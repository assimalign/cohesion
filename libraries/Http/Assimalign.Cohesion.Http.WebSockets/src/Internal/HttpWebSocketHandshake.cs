using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The protocol-independent rules of the WebSocket opening handshake (RFC 6455 §4.2): the
/// version, the key and its accept value, and the subprotocol list. Each transport's bootstrap
/// (<see cref="HttpWebSocketBootstrap"/>) applies the ones its protocol uses.
/// </summary>
internal static class HttpWebSocketHandshake
{
    /// <summary>The only WebSocket version the server speaks (RFC 6455 §4.1, §11.6).</summary>
    public const string SupportedVersion = "13";

    /// <summary>The <c>Upgrade</c> token and extended CONNECT <c>:protocol</c> of a WebSocket.</summary>
    public const string ProtocolToken = "websocket";

    // RFC 6455 §1.3: the GUID concatenated with the key before hashing.
    private const string acceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    // A Sec-WebSocket-Key is the base64 encoding of a 16-byte nonce: 24 characters, "==" padded.
    private const int keyLength = 24;
    private const int nonceLength = 16;

    // RFC 9110 §5.6.2 tchar.
    private static readonly SearchValues<char> _tokenCharacters =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Computes the <c>Sec-WebSocket-Accept</c> value for a key: the base64 encoding of the SHA-1
    /// hash of the key, as sent, concatenated with the RFC 6455 GUID (§4.2.2 item 5.4).
    /// </summary>
    /// <param name="key">The client's <c>Sec-WebSocket-Key</c>, without surrounding whitespace.</param>
    /// <returns>The value of the <c>Sec-WebSocket-Accept</c> response header.</returns>
    public static string ComputeAcceptKey(string key)
    {
        // The key is ASCII (base64), so one byte per character.
        int length = key.Length + acceptGuid.Length;
        Span<byte> input = length <= 256 ? stackalloc byte[length] : new byte[length];
        Encoding.ASCII.GetBytes(key, input);
        Encoding.ASCII.GetBytes(acceptGuid, input[key.Length..]);

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(input, hash);

        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Reads the client's <c>Sec-WebSocket-Key</c>: one field line whose value, trimmed, is the
    /// base64 encoding of exactly 16 bytes (RFC 6455 §4.2.1 item 5, §11.3.1).
    /// </summary>
    /// <param name="headers">The request headers.</param>
    /// <param name="key">The trimmed key when it is valid; otherwise empty.</param>
    /// <returns><see langword="true"/> when the request carries a valid key.</returns>
    public static bool TryGetKey(IHttpHeaderCollection headers, out string key)
    {
        key = string.Empty;

        // The field must not appear more than once (§11.3.1); several lines arrive as several values.
        if (!headers.TryGetValue(HttpHeaderKey.SecWebSocketKey, out HttpHeaderValue value) || value.Count != 1)
        {
            return false;
        }

        string? candidate = value[0]?.Trim();

        // Exactly 24 characters rules out interior whitespace, which the base64 decoder would skip.
        if (candidate is null || candidate.Length != keyLength)
        {
            return false;
        }

        // Room for two bytes more than a nonce, so a key that decodes to 17 or 18 bytes is caught.
        Span<byte> nonce = stackalloc byte[nonceLength + 2];
        if (!Convert.TryFromBase64String(candidate, nonce, out int written) || written != nonceLength)
        {
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>
    /// Gets whether the request asks for the version the server speaks:
    /// <c>Sec-WebSocket-Version</c> lists <c>13</c> (RFC 6455 §4.2.1 item 6).
    /// </summary>
    /// <param name="headers">The request headers.</param>
    /// <returns><see langword="true"/> when version 13 is requested.</returns>
    public static bool HasSupportedVersion(IHttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.SecWebSocketVersion, out HttpHeaderValue value))
        {
            return false;
        }

        foreach (string? line in value)
        {
            if (line is null)
            {
                continue;
            }

            foreach (string version in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(version, SupportedVersion, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the subprotocols the client offered in <c>Sec-WebSocket-Protocol</c>, in order. The
    /// field may span several lines; empty list elements are ignored (RFC 9110 §5.6.1).
    /// </summary>
    /// <param name="headers">The request headers.</param>
    /// <param name="protocols">The offered subprotocols; empty when none were offered or the list is malformed.</param>
    /// <returns><see langword="false"/> when an element is not a token (RFC 6455 §4.1, §11.3.4).</returns>
    public static bool TryGetRequestedProtocols(IHttpHeaderCollection headers, out IReadOnlyList<string> protocols)
    {
        protocols = Array.Empty<string>();

        if (!headers.TryGetValue(HttpHeaderKey.SecWebSocketProtocol, out HttpHeaderValue value))
        {
            return true;
        }

        List<string>? offered = null;

        foreach (string? line in value)
        {
            if (line is null)
            {
                continue;
            }

            foreach (string protocol in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsToken(protocol))
                {
                    return false;
                }

                (offered ??= new List<string>()).Add(protocol);
            }
        }

        if (offered is not null)
        {
            protocols = offered.AsReadOnly();
        }

        return true;
    }

    /// <summary>
    /// Gets whether <paramref name="value"/> is a non-empty RFC 9110 token.
    /// </summary>
    public static bool IsToken(ReadOnlySpan<char> value)
    {
        return !value.IsEmpty && !value.ContainsAnyExcept(_tokenCharacters);
    }

    /// <summary>
    /// Gets whether <paramref name="character"/> may appear in an RFC 9110 token.
    /// </summary>
    public static bool IsTokenCharacter(char character)
    {
        return _tokenCharacters.Contains(character);
    }
}
