using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3.QPack;

/// <summary>
/// The QPACK static table (RFC 9204 Appendix A) — 99 predefined
/// name/value field lines, indexed 0..98, that an encoder may reference
/// without inserting into the dynamic table. The table is read-only and
/// shared by the decoder (to resolve indexed and name-reference field
/// lines) and the encoder (to emit references rather than literals).
/// </summary>
internal static class QPackStaticTable
{
    // RFC 9204 Appendix A. Entries are (name, value); an empty value is
    // the empty string. Casing, punctuation, and spacing are reproduced
    // exactly as specified — they are part of the byte-identical match.
    private static readonly (string Name, string Value)[] Entries =
    [
        (":authority", ""),
        (":path", "/"),
        ("age", "0"),
        ("content-disposition", ""),
        ("content-length", "0"),
        ("cookie", ""),
        ("date", ""),
        ("etag", ""),
        ("if-modified-since", ""),
        ("if-none-match", ""),
        ("last-modified", ""),
        ("link", ""),
        ("location", ""),
        ("referer", ""),
        ("set-cookie", ""),
        (":method", "CONNECT"),
        (":method", "DELETE"),
        (":method", "GET"),
        (":method", "HEAD"),
        (":method", "OPTIONS"),
        (":method", "POST"),
        (":method", "PUT"),
        (":scheme", "http"),
        (":scheme", "https"),
        (":status", "103"),
        (":status", "200"),
        (":status", "304"),
        (":status", "404"),
        (":status", "503"),
        ("accept", "*/*"),
        ("accept", "application/dns-message"),
        ("accept-encoding", "gzip, deflate, br"),
        ("accept-ranges", "bytes"),
        ("access-control-allow-headers", "cache-control"),
        ("access-control-allow-headers", "content-type"),
        ("access-control-allow-origin", "*"),
        ("cache-control", "max-age=0"),
        ("cache-control", "max-age=2592000"),
        ("cache-control", "max-age=604800"),
        ("cache-control", "no-cache"),
        ("cache-control", "no-store"),
        ("cache-control", "public, max-age=31536000"),
        ("content-encoding", "br"),
        ("content-encoding", "gzip"),
        ("content-type", "application/dns-message"),
        ("content-type", "application/javascript"),
        ("content-type", "application/json"),
        ("content-type", "application/x-www-form-urlencoded"),
        ("content-type", "image/gif"),
        ("content-type", "image/jpeg"),
        ("content-type", "image/png"),
        ("content-type", "text/css"),
        ("content-type", "text/html; charset=utf-8"),
        ("content-type", "text/plain"),
        ("content-type", "text/plain;charset=utf-8"),
        ("range", "bytes=0-"),
        ("strict-transport-security", "max-age=31536000"),
        ("strict-transport-security", "max-age=31536000; includesubdomains"),
        ("strict-transport-security", "max-age=31536000; includesubdomains; preload"),
        ("vary", "accept-encoding"),
        ("vary", "origin"),
        ("x-content-type-options", "nosniff"),
        ("x-xss-protection", "1; mode=block"),
        (":status", "100"),
        (":status", "204"),
        (":status", "206"),
        (":status", "302"),
        (":status", "400"),
        (":status", "403"),
        (":status", "421"),
        (":status", "425"),
        (":status", "500"),
        ("accept-language", ""),
        ("access-control-allow-credentials", "FALSE"),
        ("access-control-allow-credentials", "TRUE"),
        ("access-control-allow-headers", "*"),
        ("access-control-allow-methods", "get"),
        ("access-control-allow-methods", "get, post, options"),
        ("access-control-allow-methods", "options"),
        ("access-control-expose-headers", "content-length"),
        ("access-control-request-headers", "content-type"),
        ("access-control-request-method", "get"),
        ("access-control-request-method", "post"),
        ("alt-svc", "clear"),
        ("authorization", ""),
        ("content-security-policy", "script-src 'none'; object-src 'none'; base-uri 'none'"),
        ("early-data", "1"),
        ("expect-ct", ""),
        ("forwarded", ""),
        ("if-range", ""),
        ("origin", ""),
        ("purpose", "prefetch"),
        ("server", ""),
        ("timing-allow-origin", "*"),
        ("upgrade-insecure-requests", "1"),
        ("user-agent", ""),
        ("x-forwarded-for", ""),
        ("x-frame-options", "deny"),
        ("x-frame-options", "sameorigin"),
    ];

    private static readonly Dictionary<string, int> NameToFirstIndex = BuildNameIndex();
    private static readonly Dictionary<(string Name, string Value), int> FieldToIndex = BuildFieldIndex();

    /// <summary>The number of entries in the static table (99).</summary>
    public static int Count => Entries.Length;

    /// <summary>
    /// Resolves a static-table index to its name and value.
    /// </summary>
    /// <param name="index">The zero-based static-table index.</param>
    /// <param name="name">The field name when the index is in range.</param>
    /// <param name="value">The field value when the index is in range.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="index"/> is a valid
    /// static-table index; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryGet(int index, out string name, out string value)
    {
        if ((uint)index < (uint)Entries.Length)
        {
            (name, value) = Entries[index];
            return true;
        }

        name = string.Empty;
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Finds the lowest static-table index whose name matches
    /// <paramref name="name"/> (used by the encoder to emit a name
    /// reference).
    /// </summary>
    /// <param name="name">The lowercase field name to look up.</param>
    /// <param name="index">The matching static-table index when found.</param>
    /// <returns>
    /// <see langword="true"/> when a name match exists; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool TryGetNameIndex(string name, out int index)
        => NameToFirstIndex.TryGetValue(name, out index);

    /// <summary>
    /// Finds the static-table index whose name and value both match
    /// (used by the encoder to emit an indexed field line).
    /// </summary>
    /// <param name="name">The lowercase field name.</param>
    /// <param name="value">The field value to match exactly.</param>
    /// <param name="index">The matching static-table index when found.</param>
    /// <returns>
    /// <see langword="true"/> when an exact name/value match exists;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryGetFieldIndex(string name, string value, out int index)
        => FieldToIndex.TryGetValue((name, value), out index);

    private static Dictionary<string, int> BuildNameIndex()
    {
        Dictionary<string, int> map = new(System.StringComparer.Ordinal);

        for (int index = 0; index < Entries.Length; index++)
        {
            // Keep the lowest index for a given name so references prefer
            // the canonical first entry.
            map.TryAdd(Entries[index].Name, index);
        }

        return map;
    }

    private static Dictionary<(string Name, string Value), int> BuildFieldIndex()
    {
        Dictionary<(string Name, string Value), int> map = new();

        for (int index = 0; index < Entries.Length; index++)
        {
            map.TryAdd(Entries[index], index);
        }

        return map;
    }
}
