using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Reads member names and string values as characters without allocating when they are unescaped and
/// short enough for a caller's buffer.
/// </summary>
internal static class JsonText
{
    /// <summary>The buffer length, in characters, callers stack-allocate for <see cref="GetName"/> and <see cref="GetString"/>.</summary>
    public const int BufferLength = 128;

    /// <summary>Returns a member's name, decoded into <paramref name="buffer"/> when it fits.</summary>
    public static ReadOnlySpan<char> GetName(JsonProperty member, Span<char> buffer)
    {
        ReadOnlySpan<byte> raw = Unquote(JsonMarshal.GetRawUtf8PropertyName(member));
        if (raw.Length <= buffer.Length && raw.IndexOf((byte)'\\') < 0)
        {
            // The parser validated the UTF-8, and a character never takes fewer bytes than code units.
            int count = Encoding.UTF8.GetChars(raw, buffer);
            return buffer.Slice(0, count);
        }

        return member.Name.AsSpan();
    }

    /// <summary>Returns a string element's value, decoded into <paramref name="buffer"/> when it fits.</summary>
    public static ReadOnlySpan<char> GetString(JsonElement element, Span<char> buffer)
    {
        ReadOnlySpan<byte> raw = Unquote(JsonMarshal.GetRawUtf8Value(element));
        if (raw.Length <= buffer.Length && raw.IndexOf((byte)'\\') < 0)
        {
            int count = Encoding.UTF8.GetChars(raw, buffer);
            return buffer.Slice(0, count);
        }

        return element.GetString().AsSpan();
    }

    private static ReadOnlySpan<byte> Unquote(ReadOnlySpan<byte> raw) =>
        raw.Length >= 2 && raw[0] == '"' ? raw.Slice(1, raw.Length - 2) : raw;
}
