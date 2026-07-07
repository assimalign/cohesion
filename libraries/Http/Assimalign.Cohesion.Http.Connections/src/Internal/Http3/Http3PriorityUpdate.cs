using System;
using System.IO;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// Parses the payload of an RFC 9218 §7.2 HTTP/3 <c>PRIORITY_UPDATE</c> frame:
/// a QUIC variable-length Prioritized Element ID followed by the ASCII Priority
/// Field Value.
/// </summary>
internal static class Http3PriorityUpdate
{
    /// <summary>
    /// Parses a PRIORITY_UPDATE frame payload into the referenced element id and
    /// its effective priority.
    /// </summary>
    /// <param name="payload">The frame payload (Prioritized Element ID + Priority Field Value).</param>
    /// <param name="prioritizedElementId">
    /// When this method returns <see langword="true"/>, the referenced request-stream
    /// (or push) identifier.
    /// </param>
    /// <param name="priority">
    /// When this method returns <see langword="true"/>, the parsed priority (with RFC 9218
    /// defaults filled in for absent members).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the payload is well-formed; <see langword="false"/> when the
    /// Prioritized Element ID or the Priority Field Value cannot be parsed, in which case the
    /// frame is ignored (RFC 9218 §7.2).
    /// </returns>
    public static bool TryParse(ReadOnlySpan<byte> payload, out long prioritizedElementId, out HttpPriority priority)
    {
        prioritizedElementId = 0;
        priority = HttpPriority.Default;

        int index = 0;
        try
        {
            prioritizedElementId = QuicVariableLengthInteger.Decode(payload, ref index);
        }
        catch (Exception exception) when (exception is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // The Prioritized Element ID varint was truncated.
            return false;
        }

        return HttpPriorityFieldValue.TryParse(payload.Slice(index), out priority);
    }
}
