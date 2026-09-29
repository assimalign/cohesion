using System;
using System.Diagnostics;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed <c>If-Range</c> conditional request header (RFC 9110 &#167; 13.1.5). Its value is
/// <em>either</em> an entity-tag <em>or</em> an HTTP-date; the two are distinguished on the wire by
/// whether the value begins with a double quote or <c>W/</c> (entity-tag) versus a date.
/// </summary>
/// <remarks>
/// <para>
/// <c>If-Range</c> lets a client that already holds part of a representation ask for the remaining
/// bytes only if the representation is unchanged, and otherwise receive the whole thing. The
/// validator it carries is always evaluated with <em>strong</em> semantics via
/// <see cref="Matches(HttpEntityTag?, DateTimeOffset?)"/>: an entity-tag is compared with
/// <see cref="HttpEntityTag.StrongEquals(HttpEntityTag)"/> (so a weak tag never matches), and a date
/// applies the range only when the representation has not been modified after the client's date.
/// </para>
/// <para>
/// This is RFC 9110 &#167; 13.2.2 step 5 (the range-application decision). It composes with steps
/// 1&#8211;4 (<c>If-Match</c> / <c>If-None-Match</c> / <c>If-*-Since</c>), which are resolved by
/// <see cref="HttpConditionalRequest.Evaluate(in HttpConditionalRequestContext)"/>: a caller runs
/// that first, and only when it returns <see cref="HttpPreconditionOutcome.Proceed"/> for a request
/// carrying a <c>Range</c> does <see cref="Matches(HttpEntityTag?, DateTimeOffset?)"/> decide whether
/// to honor the range (<c>206</c>) or ignore it and serve the full representation (<c>200</c>).
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpIfRange : IEquatable<HttpIfRange>
{
    private HttpIfRange(HttpEntityTag? entityTag, DateTimeOffset? date)
    {
        EntityTag = entityTag;
        Date = date;
    }

    /// <summary>Gets the entity-tag validator, or <see langword="null"/> when the header carries a date.</summary>
    public HttpEntityTag? EntityTag { get; }

    /// <summary>Gets the HTTP-date validator, or <see langword="null"/> when the header carries an entity-tag.</summary>
    public DateTimeOffset? Date { get; }

    /// <summary>Gets a value indicating whether the validator is an entity-tag (rather than a date).</summary>
    public bool IsEntityTag => EntityTag.HasValue;

    /// <summary>Gets a value indicating whether this instance was default-constructed (holds no validator).</summary>
    public bool IsEmpty => !EntityTag.HasValue && !Date.HasValue;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>Creates an <c>If-Range</c> that carries an entity-tag validator.</summary>
    /// <param name="entityTag">The entity-tag to compare against the current representation.</param>
    /// <returns>The <c>If-Range</c> value.</returns>
    public static HttpIfRange FromEntityTag(HttpEntityTag entityTag) => new(entityTag, null);

    /// <summary>Creates an <c>If-Range</c> that carries an HTTP-date validator.</summary>
    /// <param name="date">The date to compare against the current representation's <c>Last-Modified</c>.</param>
    /// <returns>The <c>If-Range</c> value.</returns>
    public static HttpIfRange FromDate(DateTimeOffset date) => new(null, date);

    /// <summary>
    /// Evaluates this <c>If-Range</c> validator against the target representation's current validators
    /// (RFC 9110 &#167; 13.1.5) — the RFC 9110 &#167; 13.2.2 step-5 decision of whether a present
    /// <c>Range</c> should be honored. The entity-tag form uses strong comparison (a weak or absent
    /// current tag never matches); the date form applies the range only when the representation has
    /// not been modified after the client's date, compared at one-second (HTTP-date) granularity.
    /// </summary>
    /// <param name="currentETag">The target representation's current entity-tag, or <see langword="null"/> when it has none.</param>
    /// <param name="currentLastModified">The target representation's current last-modification date, or <see langword="null"/> when unknown.</param>
    /// <returns>
    /// <see langword="true"/> when the range should be applied (serve <c>206</c>); <see langword="false"/>
    /// when the validator no longer matches and the full representation should be served (<c>200</c>).
    /// </returns>
    public bool Matches(HttpEntityTag? currentETag, DateTimeOffset? currentLastModified)
    {
        if (EntityTag is HttpEntityTag ifRangeTag)
        {
            // Strong comparison: a weak or missing current validator never applies the range.
            return currentETag is HttpEntityTag etag && ifRangeTag.StrongEquals(etag);
        }
        if (Date is DateTimeOffset ifRangeDate)
        {
            // Apply the range only if the representation has not been modified after the client's date.
            return currentLastModified is DateTimeOffset lastModified
                && TruncateToSeconds(lastModified) <= ifRangeDate;
        }
        return false;
    }

    // HTTP-date carries one-second resolution; drop any sub-second component before comparing.
    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    /// <summary>
    /// Parses an <c>If-Range</c> header value (RFC 9110 &#167; 13.1.5).
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <returns>The parsed <see cref="HttpIfRange"/>.</returns>
    /// <exception cref="HttpException">The value is neither a valid entity-tag nor a valid HTTP-date.</exception>
    public static HttpIfRange Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpIfRange result))
        {
            throw new HttpInvalidConditionalException($"The value is not a valid If-Range header: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse an <c>If-Range</c> header value (RFC 9110 &#167; 13.1.5).
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed value.</param>
    /// <returns><see langword="true"/> when the value is a valid entity-tag or HTTP-date.</returns>
    public static bool TryParse(string? value, out HttpIfRange result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse an <c>If-Range</c> header value (RFC 9110 &#167; 13.1.5).
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed value.</param>
    /// <returns><see langword="true"/> when the value is a valid entity-tag or HTTP-date.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpIfRange result)
    {
        result = default;

        ReadOnlySpan<char> trimmed = HttpFieldSyntax.TrimOws(value);
        if (trimmed.IsEmpty)
        {
            return false;
        }

        // RFC 9110 § 13.1.5: an entity-tag is distinguished from a date by a leading DQUOTE or "W/".
        bool looksLikeEntityTag = trimmed[0] == '"'
            || (trimmed.Length >= 2 && trimmed[0] == 'W' && trimmed[1] == '/');

        if (looksLikeEntityTag)
        {
            if (HttpEntityTag.TryParse(trimmed, out HttpEntityTag entityTag))
            {
                result = new HttpIfRange(entityTag, null);
                return true;
            }
            return false;
        }

        if (HttpDate.TryParse(trimmed, out DateTimeOffset date))
        {
            result = new HttpIfRange(null, date);
            return true;
        }
        return false;
    }

    /// <inheritdoc />
    public bool Equals(HttpIfRange other)
        => Nullable.Equals(EntityTag, other.EntityTag) && Nullable.Equals(Date, other.Date);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpIfRange other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(EntityTag, Date);

    /// <summary>
    /// Renders the header in wire form — the entity-tag's quoted form, or the date as an IMF-fixdate.
    /// </summary>
    /// <returns>The header value text, or an empty string for a default-constructed instance.</returns>
    public override string ToString()
    {
        if (EntityTag is HttpEntityTag tag)
        {
            return tag.ToString();
        }
        if (Date is DateTimeOffset date)
        {
            return HttpDate.Format(date);
        }
        return string.Empty;
    }

    /// <summary>Determines whether two <c>If-Range</c> values are equal.</summary>
    public static bool operator ==(HttpIfRange left, HttpIfRange right) => left.Equals(right);

    /// <summary>Determines whether two <c>If-Range</c> values are not equal.</summary>
    public static bool operator !=(HttpIfRange left, HttpIfRange right) => !left.Equals(right);
}
