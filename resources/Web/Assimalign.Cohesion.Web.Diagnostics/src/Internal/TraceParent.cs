using System;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Span-based parser for the W3C trace-context <c>traceparent</c> header
/// (<c>version "-" trace-id "-" parent-id "-" trace-flags</c>). This is the whole
/// OpenTelemetry correlation boundary for HTTP logging: the ids are surfaced as entry
/// attributes and nothing OTel-specific is referenced.
/// </summary>
internal static class TraceParent
{
    // 2 (version) + 1 + 32 (trace-id) + 1 + 16 (parent-id) + 1 + 2 (flags)
    private const int Length = 55;

    /// <summary>
    /// Attempts to extract the trace id and parent span id from a <c>traceparent</c> value.
    /// Returns <see langword="false"/> for malformed values and for the all-zero invalid ids
    /// the specification reserves.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> value, out string traceId, out string spanId)
    {
        traceId = string.Empty;
        spanId = string.Empty;

        value = value.Trim();

        // Future versions may append fields after the flags; accept them but require the
        // fixed-length prefix and its separators.
        if (value.Length < Length || (value.Length > Length && value[Length] != '-'))
        {
            return false;
        }

        if (value[2] != '-' || value[35] != '-' || value[52] != '-')
        {
            return false;
        }

        ReadOnlySpan<char> version = value[..2];
        ReadOnlySpan<char> trace = value.Slice(3, 32);
        ReadOnlySpan<char> span = value.Slice(36, 16);
        ReadOnlySpan<char> flags = value.Slice(53, 2);

        // version 0xff is forbidden by the spec.
        if (!IsLowerHex(version) || version is "ff")
        {
            return false;
        }

        if (!IsLowerHex(trace) || !IsLowerHex(span) || !IsLowerHex(flags))
        {
            return false;
        }

        if (IsAllZero(trace) || IsAllZero(span))
        {
            return false;
        }

        traceId = trace.ToString();
        spanId = span.ToString();
        return true;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (c is (< '0' or > '9') and (< 'a' or > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (c != '0')
            {
                return false;
            }
        }

        return true;
    }
}
