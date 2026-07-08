using System;
using System.Buffers;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Converts an RFC 9218 Priority Field Value — the ASCII structured-field
/// dictionary carried by an HTTP/2 or HTTP/3 <c>PRIORITY_UPDATE</c> frame — into
/// an <see cref="HttpPriority"/>. The dictionary parsing itself goes through the
/// shared core-Http structured-field toolkit; this helper only bridges the wire
/// octets (ASCII) to the <see cref="char"/> span that toolkit consumes.
/// </summary>
internal static class HttpPriorityFieldValue
{
    private const int StackallocThreshold = 256;

    /// <summary>
    /// Parses an ASCII Priority Field Value into an <see cref="HttpPriority"/>.
    /// An empty value is the empty dictionary and yields the default priority; a
    /// non-ASCII octet or an otherwise malformed structured field yields
    /// <see langword="false"/> so the caller can ignore the frame (RFC 9218 §7).
    /// </summary>
    /// <param name="asciiFieldValue">The Priority Field Value octets.</param>
    /// <param name="priority">The parsed priority, or <see cref="HttpPriority.Default"/> on failure.</param>
    /// <returns><see langword="true"/> if the value parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<byte> asciiFieldValue, out HttpPriority priority)
    {
        if (asciiFieldValue.Length == 0)
        {
            priority = HttpPriority.Default;
            return true;
        }

        char[]? rented = null;
        Span<char> chars = asciiFieldValue.Length <= StackallocThreshold
            ? stackalloc char[asciiFieldValue.Length]
            : (rented = ArrayPool<char>.Shared.Rent(asciiFieldValue.Length)).AsSpan(0, asciiFieldValue.Length);
        try
        {
            for (int i = 0; i < asciiFieldValue.Length; i++)
            {
                byte octet = asciiFieldValue[i];
                if (octet > 0x7F)
                {
                    // A non-ASCII octet cannot appear in a structured field value.
                    priority = HttpPriority.Default;
                    return false;
                }

                chars[i] = (char)octet;
            }

            return HttpPriority.TryParse(chars, out priority);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
