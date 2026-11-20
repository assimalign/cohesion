using System;
using System.Security.Cryptography;
using System.Text;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The RFC 6455 §4 opening-handshake values a proxy needs when it turns an HTTP/2 or HTTP/3 WebSocket
/// (RFC 8441, RFC 9220) into an HTTP/1.1 upgrade toward the destination.
/// </summary>
internal static class WebSocketHandshake
{
    private const string acceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>
    /// The WebSocket version RFC 6455 defines, sent when the client named none.
    /// </summary>
    public const string Version = "13";

    /// <summary>
    /// Creates a fresh <c>Sec-WebSocket-Key</c>: 16 random bytes, base64-encoded.
    /// </summary>
    public static string CreateKey()
    {
        Span<byte> key = stackalloc byte[16];
        RandomNumberGenerator.Fill(key);
        return Convert.ToBase64String(key);
    }

    /// <summary>
    /// Computes the <c>Sec-WebSocket-Accept</c> a destination must return for <paramref name="key"/>.
    /// </summary>
    public static string ComputeAccept(string key)
    {
        int length = Encoding.ASCII.GetByteCount(key) + acceptGuid.Length;
        Span<byte> input = length <= 256 ? stackalloc byte[length] : new byte[length];
        int written = Encoding.ASCII.GetBytes(key, input);
        Encoding.ASCII.GetBytes(acceptGuid, input[written..]);

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(input, hash);
        return Convert.ToBase64String(hash);
    }
}
