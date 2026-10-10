using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The trailer-section rules every HTTP version shares: which fields a received trailer section may
/// carry, and which fields an application may stage in a response's trailer section.
/// </summary>
/// <remarks>
/// <para>
/// One rule set for every version, in both directions. A trailer section carries no connection-specific
/// field (RFC 9113 §8.2.2, RFC 9114 §4.2) and none of the fields RFC 9110 §6.5.1 excludes from
/// trailers (<see cref="HttpFieldRules.IsProhibitedInTrailers"/>: framing, routing, request
/// modifiers, authentication, response controls, content processing, and <c>Trailer</c> itself).
/// HTTP/2 and HTTP/3 field sections also carry no pseudo-header field (RFC 9113 §8.1, RFC 9114 §4.3),
/// and their fields follow the field syntax a head's do (<see cref="HttpReceivedFieldRules"/>, #1376):
/// lowercase token names (RFC 9113 §8.2.1, RFC 9114 §4.2) and values with no control character but
/// HTAB and no whitespace at either end.
/// </para>
/// <para>
/// HTTP/1.1 applies <see cref="EnsureReceivable"/> to each field of a chunked trailer section (#1319).
/// The pseudo-header and lowercase rules have no HTTP/1.1 counterpart to apply: its field names are
/// case-insensitive, and a name that starts with <c>:</c> is not a field name at all, so its reader
/// rejects such a line as malformed. Its field syntax is the header section's (<c>Http1FieldLine</c>,
/// #1341). HTTP/1.1 sends no response trailers.
/// </para>
/// </remarks>
internal static class HttpTrailerFieldRules
{
    /// <summary>
    /// Rejects a field an application stages in a response's trailer section when HTTP/2 and HTTP/3
    /// cannot send it there, so the mistake surfaces where it is made rather than on the wire.
    /// </summary>
    /// <remarks>
    /// The field syntax (#1183) is checked here too, for the same reason. The encoders check it again
    /// when they write the section (<see cref="HttpResponseFieldRules"/>), because a value built over an
    /// array can change after it was staged.
    /// </remarks>
    /// <param name="key">The field name being added.</param>
    /// <param name="value">The field value being added.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is empty, a pseudo-header field, not a token, a connection-specific field,
    /// or a field prohibited in trailers; or <paramref name="value"/> holds a control character other than
    /// HTAB (RFC 9110 §5.5).
    /// </exception>
    public static void EnsureSendable(HttpHeaderKey key, HttpHeaderValue value)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("A trailer field name cannot be empty.", nameof(key));
        }

        if (key.Value[0] == ':')
        {
            throw new ArgumentException(
                $"'{key.Value}' is a pseudo-header field, and a trailer section carries none (RFC 9113 §8.1, RFC 9114 §4.3).",
                nameof(key));
        }

        if (!HttpFieldNormalization.IsValidFieldName(key.Value))
        {
            // Not quoted: whatever makes the name invalid may be CR, LF, or NUL.
            throw new ArgumentException("A trailer field name must be a token (RFC 9110 §5.1).", nameof(key));
        }

        if (IsExcluded(key))
        {
            throw new ArgumentException(
                $"The field '{key.Value}' cannot be sent in a trailer section (RFC 9110 §6.5.1, RFC 9113 §8.2.2, RFC 9114 §4.2).",
                nameof(key));
        }

        foreach (string? element in value)
        {
            int invalid = HttpFieldNormalization.IndexOfInvalidControlCharacter(element);

            if (invalid >= 0)
            {
                throw new ArgumentException(
                    $"The value of the trailer field '{key.Value}' holds the control character 0x{(int)element![invalid]:X2}; a field value holds no control character but HTAB (RFC 9110 §5.5).",
                    nameof(value));
            }
        }
    }

    /// <summary>
    /// Validates the decoded field lines of a received trailer section and adds them to
    /// <paramref name="trailers"/>, combining a repeated field as a header section does.
    /// </summary>
    /// <param name="fields">The decoded field lines, in wire order.</param>
    /// <param name="trailers">The collection that receives the fields.</param>
    /// <param name="protocol">The protocol named in a violation message: <c>HTTP/2</c> or <c>HTTP/3</c>.</param>
    /// <exception cref="InvalidDataException">
    /// The section is malformed: a zero-length field name, a pseudo-header field, a name that is not a
    /// lowercase token or a value with NUL, CR, LF, another control character but HTAB, or whitespace
    /// at either end (RFC 9113 §8.2.1, RFC 9114 §4.2, <see cref="HttpReceivedFieldRules"/>, #1376), a
    /// connection-specific field, or a field prohibited in trailers. The caller raises its protocol's
    /// stream error.
    /// </exception>
    public static void AddReceivedFields(List<(string Name, string Value)> fields, IHttpHeaderCollection trailers, string protocol)
    {
        foreach ((string name, string value) in fields)
        {
            if (name.Length == 0)
            {
                throw new InvalidDataException($"The {protocol} trailer section contains a zero-length field name.");
            }

            if (name[0] == ':')
            {
                throw new InvalidDataException(
                    $"The {protocol} trailer section contains the pseudo-header field {HttpReceivedFieldRules.DescribeName(name)}; a trailer section carries none (RFC 9113 §8.1, RFC 9114 §4.3).");
            }

            // The field syntax a head's fields follow (#1376): a lowercase token name, and a value with
            // no control character but HTAB and no whitespace at either end.
            HttpReceivedFieldRules.EnsureValidName(name, protocol);
            HttpReceivedFieldRules.EnsureValidValue(name, value, protocol);

            HttpHeaderKey key = new(name);
            EnsureReceivable(key, protocol);

            if (trailers.TryGetValue(key, out HttpHeaderValue existingValue))
            {
                trailers[key] = HttpFieldNormalization.CombineFieldValue(key, existingValue, value);
            }
            else
            {
                trailers[key] = value;
            }
        }
    }

    /// <summary>
    /// Rejects a field that a received trailer section cannot carry, on any version: a
    /// connection-specific field, or a field RFC 9110 §6.5.1 excludes from trailers. HTTP/1.1 calls
    /// this for each field of a chunked trailer section; <see cref="AddReceivedFields"/> calls it for
    /// HTTP/2 and HTTP/3.
    /// </summary>
    /// <param name="key">The received field name.</param>
    /// <param name="protocol">The protocol named in a violation message: <c>HTTP/1.1</c>, <c>HTTP/2</c> or <c>HTTP/3</c>.</param>
    /// <exception cref="InvalidDataException">
    /// The field cannot be carried in a trailer section. The caller fails the request through its
    /// protocol's malformed-message path.
    /// </exception>
    public static void EnsureReceivable(HttpHeaderKey key, string protocol)
    {
        if (IsExcluded(key))
        {
            throw new InvalidDataException(
                $"The {protocol} trailer section contains the field '{key.Value}', which a trailer section cannot carry (RFC 9110 §6.5.1, RFC 9113 §8.2.2, RFC 9114 §4.2).");
        }
    }

    private static bool IsExcluded(HttpHeaderKey key)
    {
        return HttpFieldRules.IsProhibitedInTrailers(key) || HttpFieldNormalization.IsForbiddenInHttp2Or3(key);
    }
}
