using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// AOT/trim-safe binary framing for a session's key/value dictionary. The frame
/// is a version-prefixed, length-prefixed layout with no reflection and no
/// dynamic serialization, so the exact same bytes are produced and consumed by
/// any <see cref="IHttpSessionStore"/> — the property that lets a session move
/// between an in-process and an out-of-process backend unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Framing deliberately lives with the session rather than the store: a store
/// sees only opaque bytes (<see cref="IHttpSessionStore"/>), while the encoding
/// rules — and their forward evolution — live here in one place.
/// </para>
/// <para>
/// <b>Frame layout (version 1).</b>
/// <code>
/// [1]  version           (0x01)
/// [4]  entry count N      (big-endian int32)
/// repeat N times:
///   [4]  key length K     (big-endian int32)
///   [K]  key              (UTF-8)
///   [4]  value length V   (big-endian int32)
///   [V]  value            (raw bytes)
/// </code>
/// Integers are big-endian (network order) so a frame written on one platform
/// reads back identically on another. The leading version byte lets the frame
/// evolve: a reader that does not recognize the version rejects the frame
/// (<see cref="TryDeserialize"/> returns <see langword="false"/>) rather than
/// misinterpreting it.
/// </para>
/// </remarks>
public static class HttpSessionSerializer
{
    /// <summary>
    /// The current frame-format version written by <see cref="Serialize"/>.
    /// </summary>
    public const byte Version = 1;

    private const int HeaderLength = 1 + sizeof(int);
    private const int LengthPrefix = sizeof(int);

    /// <summary>
    /// Serializes a session's entries into a self-describing binary frame.
    /// </summary>
    /// <param name="values">The session's key/value entries.</param>
    /// <returns>The encoded frame. An empty session yields a valid five-byte frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>, or any entry value is <see langword="null"/>.</exception>
    public static byte[] Serialize(IReadOnlyDictionary<string, byte[]> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        int size = HeaderLength;
        foreach (KeyValuePair<string, byte[]> entry in values)
        {
            ArgumentNullException.ThrowIfNull(entry.Value);
            size += LengthPrefix + Encoding.UTF8.GetByteCount(entry.Key) + LengthPrefix + entry.Value.Length;
        }

        byte[] buffer = new byte[size];
        Span<byte> span = buffer;

        span[0] = Version;
        span = span[1..];
        BinaryPrimitives.WriteInt32BigEndian(span, values.Count);
        span = span[sizeof(int)..];

        foreach (KeyValuePair<string, byte[]> entry in values)
        {
            int keyLength = Encoding.UTF8.GetByteCount(entry.Key);
            BinaryPrimitives.WriteInt32BigEndian(span, keyLength);
            span = span[sizeof(int)..];
            Encoding.UTF8.GetBytes(entry.Key, span);
            span = span[keyLength..];

            BinaryPrimitives.WriteInt32BigEndian(span, entry.Value.Length);
            span = span[sizeof(int)..];
            entry.Value.CopyTo(span);
            span = span[entry.Value.Length..];
        }

        return buffer;
    }

    /// <summary>
    /// Attempts to decode a session frame produced by <see cref="Serialize"/>.
    /// </summary>
    /// <param name="frame">The frame bytes.</param>
    /// <param name="values">The decoded entries when the frame is well-formed.</param>
    /// <returns>
    /// <see langword="true"/> when the frame is a valid version-1 frame;
    /// <see langword="false"/> when the version is unrecognized, a declared length
    /// runs past the buffer, or trailing bytes remain. A false result carries no
    /// entries — callers treat an unreadable frame as an empty session rather than
    /// faulting the request.
    /// </returns>
    public static bool TryDeserialize(ReadOnlySpan<byte> frame, [NotNullWhen(true)] out Dictionary<string, byte[]>? values)
    {
        values = null;

        if (frame.Length < HeaderLength || frame[0] != Version)
        {
            return false;
        }

        int position = 1;
        int count = BinaryPrimitives.ReadInt32BigEndian(frame[position..]);
        position += sizeof(int);

        if (count < 0)
        {
            return false;
        }

        // The count is not trusted for pre-sizing: a hostile frame could declare a
        // huge count with no backing bytes, so the dictionary grows only as real
        // entries are read and bounds-checked.
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            if (!TryReadLength(frame, ref position, out int keyLength) ||
                frame.Length - position < keyLength)
            {
                return false;
            }

            string key = Encoding.UTF8.GetString(frame.Slice(position, keyLength));
            position += keyLength;

            if (!TryReadLength(frame, ref position, out int valueLength) ||
                frame.Length - position < valueLength)
            {
                return false;
            }

            byte[] value = frame.Slice(position, valueLength).ToArray();
            position += valueLength;

            result[key] = value;
        }

        // A well-formed frame is consumed exactly; leftover bytes signal corruption.
        if (position != frame.Length)
        {
            return false;
        }

        values = result;
        return true;
    }

    private static bool TryReadLength(ReadOnlySpan<byte> frame, ref int position, out int length)
    {
        if (frame.Length - position < sizeof(int))
        {
            length = 0;
            return false;
        }

        length = BinaryPrimitives.ReadInt32BigEndian(frame[position..]);
        position += sizeof(int);
        return length >= 0;
    }
}
