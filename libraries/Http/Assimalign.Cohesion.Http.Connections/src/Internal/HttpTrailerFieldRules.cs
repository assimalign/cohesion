using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The trailer-section rules the HTTP/2 and HTTP/3 transports share: which fields a received trailer
/// section may carry.
/// </summary>
/// <remarks>
/// <para>
/// One rule set for both versions. A trailer section carries no pseudo-header field (RFC 9113 §8.1,
/// RFC 9114 §4.3), no connection-specific field (RFC 9113 §8.2.2, RFC 9114 §4.2), and none of the
/// fields RFC 9110 §6.5.1 excludes from trailers (<see cref="HttpFieldRules.IsProhibitedInTrailers"/>:
/// framing, routing, request modifiers, authentication, response controls, content processing, and
/// <c>Trailer</c> itself).
/// </para>
/// <para>
/// HTTP/1.1 does not use these rules: its chunked request reader rejects only the framing and routing
/// fields a trailer could use to smuggle a request (RFC 9112 §7.1.2).
/// </para>
/// </remarks>
internal static class HttpTrailerFieldRules
{
    /// <summary>
    /// Validates the decoded field lines of a received trailer section and adds them to
    /// <paramref name="trailers"/>, combining a repeated field as a header section does.
    /// </summary>
    /// <param name="fields">The decoded field lines, in wire order.</param>
    /// <param name="trailers">The collection that receives the fields.</param>
    /// <param name="protocol">The protocol named in a violation message: <c>HTTP/2</c> or <c>HTTP/3</c>.</param>
    /// <exception cref="InvalidDataException">
    /// The section is malformed: a zero-length or uppercase field name (RFC 9113 §8.2.1, RFC 9114 §4.2),
    /// a pseudo-header field, a connection-specific field, or a field prohibited in trailers. The caller
    /// raises its protocol's stream error.
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
                    $"The {protocol} trailer section contains the pseudo-header field '{name}'; a trailer section carries none (RFC 9113 §8.1, RFC 9114 §4.3).");
            }

            if (!IsLowercase(name))
            {
                throw new InvalidDataException(
                    $"The {protocol} trailer field name '{name}' must be lowercase (RFC 9113 §8.2.1, RFC 9114 §4.2).");
            }

            HttpHeaderKey key = new(name);

            if (IsExcluded(key))
            {
                throw new InvalidDataException(
                    $"The {protocol} trailer section contains the field '{name}', which a trailer section cannot carry (RFC 9110 §6.5.1, RFC 9113 §8.2.2, RFC 9114 §4.2).");
            }

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

    private static bool IsExcluded(HttpHeaderKey key)
    {
        return HttpFieldRules.IsProhibitedInTrailers(key) || HttpFieldNormalization.IsForbiddenInHttp2Or3(key);
    }

    private static bool IsLowercase(string name)
    {
        foreach (char character in name)
        {
            if (character is >= 'A' and <= 'Z')
            {
                return false;
            }
        }

        return true;
    }
}
