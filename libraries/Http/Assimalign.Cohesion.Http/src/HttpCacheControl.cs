using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed <c>Cache-Control</c> field value (RFC 9111 &#167; 5.2): the request and response
/// cache directives that govern caching behavior. The same value object models both directions —
/// a consumer reads the directives relevant to the message it is handling.
/// </summary>
/// <remarks>
/// <para>
/// Recognized directives are surfaced as typed members: boolean directives as <c>bool</c>
/// properties, delta-seconds directives (RFC 9111 &#167; 1.2.2) as <see cref="TimeSpan"/> values,
/// and the <c>no-cache</c> / <c>private</c> response field-name arguments as string lists.
/// Unrecognized directives are preserved in <see cref="Extensions"/> (RFC 9111 &#167; 5.2.3) so a
/// parse-then-serialize round-trip does not lose them. A delta-seconds value larger than can be
/// represented is retained as roughly 68 years (2^31 &#8722; 1 seconds) per RFC 9111 &#167; 1.2.2.
/// </para>
/// <para>
/// Parsing is a single span-based pass. <see cref="TryParse(ReadOnlySpan{char}, out HttpCacheControl)"/>
/// returns <see langword="false"/> for structurally malformed input — a non-token directive name, or a
/// numeric directive whose argument is not a valid delta-seconds — rather than throwing; empty list
/// elements are ignored (RFC 9110 &#167; 5.6.1.2).
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpCacheControl
{
    private const long MaxDeltaSeconds = int.MaxValue;

    private readonly HttpCacheControlFlags flags;
    private readonly string[]? noCacheFields;
    private readonly string[]? privateFields;
    private readonly HttpCacheControlExtension[]? extensions;

    private HttpCacheControl(
        HttpCacheControlFlags flags,
        TimeSpan? maxAge,
        TimeSpan? sharedMaxAge,
        TimeSpan? minFresh,
        TimeSpan? maxStale,
        TimeSpan? staleWhileRevalidate,
        TimeSpan? staleIfError,
        string[]? noCacheFields,
        string[]? privateFields,
        HttpCacheControlExtension[]? extensions)
    {
        this.flags = flags;
        MaxAge = maxAge;
        SharedMaxAge = sharedMaxAge;
        MinFresh = minFresh;
        MaxStale = maxStale;
        StaleWhileRevalidate = staleWhileRevalidate;
        StaleIfError = staleIfError;
        this.noCacheFields = noCacheFields;
        this.privateFields = privateFields;
        this.extensions = extensions;
    }

    /// <summary>Gets a value indicating whether the <c>no-store</c> directive is present.</summary>
    public bool NoStore => (flags & HttpCacheControlFlags.NoStore) != 0;

    /// <summary>Gets a value indicating whether the <c>no-cache</c> directive is present.</summary>
    public bool NoCache => (flags & HttpCacheControlFlags.NoCache) != 0;

    /// <summary>Gets a value indicating whether the <c>no-transform</c> directive is present.</summary>
    public bool NoTransform => (flags & HttpCacheControlFlags.NoTransform) != 0;

    /// <summary>Gets a value indicating whether the response <c>public</c> directive is present.</summary>
    public bool Public => (flags & HttpCacheControlFlags.Public) != 0;

    /// <summary>Gets a value indicating whether the response <c>private</c> directive is present.</summary>
    public bool Private => (flags & HttpCacheControlFlags.Private) != 0;

    /// <summary>Gets a value indicating whether the response <c>must-revalidate</c> directive is present.</summary>
    public bool MustRevalidate => (flags & HttpCacheControlFlags.MustRevalidate) != 0;

    /// <summary>Gets a value indicating whether the response <c>proxy-revalidate</c> directive is present.</summary>
    public bool ProxyRevalidate => (flags & HttpCacheControlFlags.ProxyRevalidate) != 0;

    /// <summary>Gets a value indicating whether the response <c>must-understand</c> directive is present.</summary>
    public bool MustUnderstand => (flags & HttpCacheControlFlags.MustUnderstand) != 0;

    /// <summary>Gets a value indicating whether the response <c>immutable</c> directive (RFC 8246) is present.</summary>
    public bool Immutable => (flags & HttpCacheControlFlags.Immutable) != 0;

    /// <summary>Gets a value indicating whether the request <c>only-if-cached</c> directive is present.</summary>
    public bool OnlyIfCached => (flags & HttpCacheControlFlags.OnlyIfCached) != 0;

    /// <summary>
    /// Gets a value indicating whether the request <c>max-stale</c> directive is present. When
    /// <see langword="true"/> and <see cref="MaxStale"/> is <see langword="null"/>, the client
    /// accepts a response of any staleness.
    /// </summary>
    public bool HasMaxStale => (flags & HttpCacheControlFlags.MaxStale) != 0;

    /// <summary>Gets the <c>max-age</c> directive value, or <see langword="null"/> when absent.</summary>
    public TimeSpan? MaxAge { get; }

    /// <summary>Gets the response <c>s-maxage</c> directive value, or <see langword="null"/> when absent.</summary>
    public TimeSpan? SharedMaxAge { get; }

    /// <summary>Gets the request <c>min-fresh</c> directive value, or <see langword="null"/> when absent.</summary>
    public TimeSpan? MinFresh { get; }

    /// <summary>
    /// Gets the request <c>max-stale</c> directive value. <see langword="null"/> means either the
    /// directive is absent (see <see cref="HasMaxStale"/>) or it was present without a value
    /// (accept any staleness).
    /// </summary>
    public TimeSpan? MaxStale { get; }

    /// <summary>Gets the <c>stale-while-revalidate</c> directive value (RFC 5861), or <see langword="null"/> when absent.</summary>
    public TimeSpan? StaleWhileRevalidate { get; }

    /// <summary>Gets the <c>stale-if-error</c> directive value (RFC 5861), or <see langword="null"/> when absent.</summary>
    public TimeSpan? StaleIfError { get; }

    /// <summary>
    /// Gets the field names carried by a response <c>no-cache="&#8230;"</c> argument. Empty when the
    /// directive is absent or carries no argument.
    /// </summary>
    public IReadOnlyList<string> NoCacheFields => noCacheFields ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// Gets the field names carried by a response <c>private="&#8230;"</c> argument. Empty when the
    /// directive is absent or carries no argument.
    /// </summary>
    public IReadOnlyList<string> PrivateFields => privateFields ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// Gets the unrecognized extension directives (RFC 9111 &#167; 5.2.3) preserved from the field.
    /// </summary>
    public IReadOnlyList<HttpCacheControlExtension> Extensions
        => extensions ?? (IReadOnlyList<HttpCacheControlExtension>)Array.Empty<HttpCacheControlExtension>();

    /// <summary>
    /// Gets a value indicating whether no directives at all are present (a default-constructed value).
    /// </summary>
    public bool IsEmpty
        => flags == HttpCacheControlFlags.None
        && MaxAge is null && SharedMaxAge is null && MinFresh is null && MaxStale is null
        && StaleWhileRevalidate is null && StaleIfError is null
        && noCacheFields is null && privateFields is null && extensions is null;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Parses <paramref name="value"/> as a <c>Cache-Control</c> field value.
    /// </summary>
    /// <param name="value">The field text (e.g. <c>max-age=60, must-revalidate</c>).</param>
    /// <returns>The parsed <see cref="HttpCacheControl"/>.</returns>
    /// <exception cref="HttpException">The value is not a well-formed <c>Cache-Control</c> field.</exception>
    public static HttpCacheControl Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpCacheControl result))
        {
            throw new HttpInvalidCacheControlException($"The value is not a valid Cache-Control field: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a <c>Cache-Control</c> field value.
    /// </summary>
    /// <param name="value">The field text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed directives.</param>
    /// <returns><see langword="true"/> when the value is a well-formed <c>Cache-Control</c> field.</returns>
    public static bool TryParse(string? value, out HttpCacheControl result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a <c>Cache-Control</c> field value. A non-token
    /// directive name or a malformed delta-seconds argument fails the parse; empty list elements are
    /// ignored.
    /// </summary>
    /// <param name="value">The field text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed directives.</param>
    /// <returns><see langword="true"/> when the value is a well-formed <c>Cache-Control</c> field.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpCacheControl result)
    {
        result = default;

        ReadOnlySpan<char> span = HttpFieldSyntax.TrimOws(value);
        if (span.IsEmpty)
        {
            return false;
        }

        HttpCacheControlFlags flags = HttpCacheControlFlags.None;
        TimeSpan? maxAge = null, sharedMaxAge = null, minFresh = null, maxStale = null, staleWhileRevalidate = null, staleIfError = null;
        string[]? noCacheFields = null;
        string[]? privateFields = null;
        List<HttpCacheControlExtension>? extensions = null;
        bool anyDirective = false;

        while (!span.IsEmpty)
        {
            int comma = HttpFieldSyntax.IndexOfUnquoted(span, ',');
            ReadOnlySpan<char> segment = HttpFieldSyntax.TrimOws(comma < 0 ? span : span[..comma]);
            span = comma < 0 ? ReadOnlySpan<char>.Empty : span[(comma + 1)..];

            if (segment.IsEmpty)
            {
                continue;
            }

            int equals = HttpFieldSyntax.IndexOfUnquoted(segment, '=');
            ReadOnlySpan<char> nameSpan = HttpFieldSyntax.TrimOws(equals < 0 ? segment : segment[..equals]);
            ReadOnlySpan<char> valueSpan = equals < 0 ? default : HttpFieldSyntax.TrimOws(segment[(equals + 1)..]);
            bool hasValue = equals >= 0;

            if (!HttpFieldSyntax.IsToken(nameSpan))
            {
                return false;
            }

            string name = ToLower(nameSpan);
            switch (name)
            {
                case "no-store":
                    flags |= HttpCacheControlFlags.NoStore;
                    break;
                case "no-transform":
                    flags |= HttpCacheControlFlags.NoTransform;
                    break;
                case "public":
                    flags |= HttpCacheControlFlags.Public;
                    break;
                case "must-revalidate":
                    flags |= HttpCacheControlFlags.MustRevalidate;
                    break;
                case "proxy-revalidate":
                    flags |= HttpCacheControlFlags.ProxyRevalidate;
                    break;
                case "must-understand":
                    flags |= HttpCacheControlFlags.MustUnderstand;
                    break;
                case "immutable":
                    flags |= HttpCacheControlFlags.Immutable;
                    break;
                case "only-if-cached":
                    flags |= HttpCacheControlFlags.OnlyIfCached;
                    break;
                case "no-cache":
                    flags |= HttpCacheControlFlags.NoCache;
                    if (hasValue && !valueSpan.IsEmpty)
                    {
                        noCacheFields = ParseFieldList(valueSpan);
                    }
                    break;
                case "private":
                    flags |= HttpCacheControlFlags.Private;
                    if (hasValue && !valueSpan.IsEmpty)
                    {
                        privateFields = ParseFieldList(valueSpan);
                    }
                    break;
                case "max-age":
                    if (!hasValue || !TryParseDeltaSeconds(valueSpan, out TimeSpan ma))
                    {
                        return false;
                    }
                    maxAge = ma;
                    break;
                case "s-maxage":
                    if (!hasValue || !TryParseDeltaSeconds(valueSpan, out TimeSpan sm))
                    {
                        return false;
                    }
                    sharedMaxAge = sm;
                    break;
                case "min-fresh":
                    if (!hasValue || !TryParseDeltaSeconds(valueSpan, out TimeSpan mf))
                    {
                        return false;
                    }
                    minFresh = mf;
                    break;
                case "max-stale":
                    flags |= HttpCacheControlFlags.MaxStale;
                    if (hasValue && !valueSpan.IsEmpty)
                    {
                        if (!TryParseDeltaSeconds(valueSpan, out TimeSpan ms))
                        {
                            return false;
                        }
                        maxStale = ms;
                    }
                    break;
                case "stale-while-revalidate":
                    if (!hasValue || !TryParseDeltaSeconds(valueSpan, out TimeSpan swr))
                    {
                        return false;
                    }
                    staleWhileRevalidate = swr;
                    break;
                case "stale-if-error":
                    if (!hasValue || !TryParseDeltaSeconds(valueSpan, out TimeSpan sie))
                    {
                        return false;
                    }
                    staleIfError = sie;
                    break;
                default:
                    string? extensionValue = hasValue ? HttpFieldSyntax.UnquoteValue(valueSpan) : null;
                    (extensions ??= new List<HttpCacheControlExtension>()).Add(new HttpCacheControlExtension(name, extensionValue));
                    break;
            }

            anyDirective = true;
        }

        if (!anyDirective)
        {
            return false;
        }

        result = new HttpCacheControl(
            flags, maxAge, sharedMaxAge, minFresh, maxStale, staleWhileRevalidate, staleIfError,
            noCacheFields, privateFields, extensions?.ToArray());
        return true;
    }

    private static string[] ParseFieldList(ReadOnlySpan<char> value)
    {
        // The no-cache / private argument is a quoted-string wrapping a comma-separated field list
        // (RFC 9111 §5.2.2.2 / §5.2.2.7). This argument is uncommon, so the split is done on the
        // materialized inner string rather than the hot span path.
        string inner = HttpFieldSyntax.UnquoteValue(value);
        List<string>? fields = null;
        foreach (Range range in Split(inner))
        {
            ReadOnlySpan<char> field = HttpFieldSyntax.TrimOws(inner.AsSpan()[range]);
            if (HttpFieldSyntax.IsToken(field))
            {
                (fields ??= new List<string>()).Add(field.ToString());
            }
        }
        return fields?.ToArray() ?? Array.Empty<string>();
    }

    private static IEnumerable<Range> Split(string value)
    {
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == ',')
            {
                yield return new Range(start, i);
                start = i + 1;
            }
        }
        yield return new Range(start, value.Length);
    }

    private static bool TryParseDeltaSeconds(ReadOnlySpan<char> value, out TimeSpan result)
    {
        result = default;

        ReadOnlySpan<char> digits = value;
        // Tolerate a quoted delta-seconds ("3600") that some senders emit.
        if (digits.Length >= 2 && digits[0] == '"' && digits[^1] == '"')
        {
            digits = digits[1..^1];
        }
        if (digits.IsEmpty)
        {
            return false;
        }

        long seconds = 0;
        bool clamped = false;
        foreach (char c in digits)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
            if (!clamped)
            {
                seconds = (seconds * 10) + (c - '0');
                if (seconds >= MaxDeltaSeconds)
                {
                    seconds = MaxDeltaSeconds;
                    clamped = true;
                }
            }
        }

        result = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static string ToLower(ReadOnlySpan<char> span)
    {
        Span<char> buffer = span.Length <= 64 ? stackalloc char[span.Length] : new char[span.Length];
        int written = span.ToLowerInvariant(buffer);
        return new string(buffer[..written]);
    }

    /// <summary>
    /// Renders the directives in canonical <c>Cache-Control</c> wire form. Re-parsing the result
    /// yields an equivalent value.
    /// </summary>
    /// <returns>The comma-separated directive list.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();

        AppendFlag(builder, NoStore, "no-store");
        AppendNoCache(builder);
        AppendFlag(builder, NoTransform, "no-transform");
        AppendFlag(builder, Public, "public");
        AppendPrivate(builder);
        AppendFlag(builder, MustRevalidate, "must-revalidate");
        AppendFlag(builder, ProxyRevalidate, "proxy-revalidate");
        AppendFlag(builder, MustUnderstand, "must-understand");
        AppendFlag(builder, Immutable, "immutable");
        AppendFlag(builder, OnlyIfCached, "only-if-cached");
        AppendDelta(builder, "max-age", MaxAge);
        AppendDelta(builder, "s-maxage", SharedMaxAge);
        AppendDelta(builder, "min-fresh", MinFresh);
        if (HasMaxStale)
        {
            if (MaxStale is { } maxStale)
            {
                AppendDelta(builder, "max-stale", maxStale);
            }
            else
            {
                AppendName(builder, "max-stale");
            }
        }
        AppendDelta(builder, "stale-while-revalidate", StaleWhileRevalidate);
        AppendDelta(builder, "stale-if-error", StaleIfError);

        if (extensions is not null)
        {
            foreach (HttpCacheControlExtension extension in extensions)
            {
                Separate(builder);
                builder.Append(extension.Name);
                if (extension.Value is { } extensionValue)
                {
                    builder.Append('=');
                    AppendArgument(builder, extensionValue);
                }
            }
        }

        return builder.ToString();
    }

    private static void AppendFlag(StringBuilder builder, bool present, string name)
    {
        if (present)
        {
            AppendName(builder, name);
        }
    }

    private void AppendNoCache(StringBuilder builder)
    {
        if (!NoCache)
        {
            return;
        }
        Separate(builder);
        builder.Append("no-cache");
        AppendFieldList(builder, noCacheFields);
    }

    private void AppendPrivate(StringBuilder builder)
    {
        if (!Private)
        {
            return;
        }
        Separate(builder);
        builder.Append("private");
        AppendFieldList(builder, privateFields);
    }

    private static void AppendFieldList(StringBuilder builder, string[]? fields)
    {
        if (fields is null || fields.Length == 0)
        {
            return;
        }
        builder.Append("=\"");
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }
            builder.Append(fields[i]);
        }
        builder.Append('"');
    }

    private static void AppendDelta(StringBuilder builder, string name, TimeSpan? value)
    {
        if (value is { } span)
        {
            Separate(builder);
            builder.Append(name).Append('=').Append(((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendName(StringBuilder builder, string name)
    {
        Separate(builder);
        builder.Append(name);
    }

    private static void AppendArgument(StringBuilder builder, string value)
    {
        if (value.Length > 0 && HttpFieldSyntax.IsToken(value.AsSpan()))
        {
            builder.Append(value);
            return;
        }
        builder.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        builder.Append('"');
    }

    private static void Separate(StringBuilder builder)
    {
        if (builder.Length > 0)
        {
            builder.Append(", ");
        }
    }
}
