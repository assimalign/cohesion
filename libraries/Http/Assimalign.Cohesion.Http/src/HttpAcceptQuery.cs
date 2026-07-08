using System;
using System.Collections.Generic;
using System.Diagnostics;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed <c>Accept-Query</c> response header field value (RFC 10008 &#167; 3): the ordered list
/// of query-format media ranges a resource accepts in the content of a <see cref="HttpMethod.Query"/>
/// request. Its presence also signals that the resource supports the QUERY method.
/// </summary>
/// <remarks>
/// <para>
/// The field is an RFC 9651 Structured Field List whose members are media ranges expressed as either
/// Tokens or Strings — "the choice of Token versus String is semantically insignificant" (RFC 10008
/// &#167; 3) — with any media-type parameters carried as structured-field parameters. Each member is
/// projected onto the core <see cref="HttpMediaType"/> value object, so a consumer negotiates a QUERY
/// content type against the same media-range machinery used for <c>Accept</c> and <c>Content-Type</c>.
/// </para>
/// <para>
/// Parsing goes <em>through</em> <see cref="StructuredFieldList"/> rather than around it — there is one
/// structured-field parser in the stack, and this value object only adds the field's media-range
/// semantics on top of it. The header name is exposed as the typed <see cref="HttpHeaderKey.AcceptQuery"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpAcceptQuery : IEquatable<HttpAcceptQuery>
{
    private readonly HttpMediaType[]? mediaRanges;

    /// <summary>An empty <c>Accept-Query</c> value that advertises no media ranges.</summary>
    public static HttpAcceptQuery Empty => default;

    private HttpAcceptQuery(HttpMediaType[]? mediaRanges)
    {
        this.mediaRanges = mediaRanges is { Length: > 0 } ? mediaRanges : null;
    }

    /// <summary>
    /// Initializes an <c>Accept-Query</c> value from an ordered sequence of accepted media ranges.
    /// </summary>
    /// <param name="mediaRanges">The accepted media ranges, in advertised order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mediaRanges"/> is <see langword="null"/>.</exception>
    public HttpAcceptQuery(IEnumerable<HttpMediaType> mediaRanges)
    {
        ArgumentNullException.ThrowIfNull(mediaRanges);
        var buffer = new List<HttpMediaType>();
        foreach (HttpMediaType mediaRange in mediaRanges)
        {
            buffer.Add(mediaRange);
        }
        this.mediaRanges = buffer.Count == 0 ? null : buffer.ToArray();
    }

    /// <summary>
    /// Gets the accepted query-format media ranges, in advertised order. Empty when the field carries
    /// no members.
    /// </summary>
    public IReadOnlyList<HttpMediaType> MediaRanges
        => mediaRanges ?? (IReadOnlyList<HttpMediaType>)Array.Empty<HttpMediaType>();

    /// <summary>Gets the number of advertised media ranges.</summary>
    public int Count => mediaRanges?.Length ?? 0;

    /// <summary>Gets a value indicating whether the field advertises no media ranges.</summary>
    public bool IsEmpty => mediaRanges is null;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Determines whether <paramref name="contentType"/> is acceptable as QUERY request content —
    /// that is, whether any advertised media range includes it (RFC 9110 &#167; 12.5.1 matching).
    /// </summary>
    /// <param name="contentType">The concrete content media type to test.</param>
    /// <returns>
    /// <see langword="true"/> when an advertised range includes <paramref name="contentType"/>;
    /// otherwise <see langword="false"/>. An empty field accepts nothing and returns
    /// <see langword="false"/>.
    /// </returns>
    public bool Accepts(HttpMediaType contentType)
    {
        if (mediaRanges is null)
        {
            return false;
        }
        foreach (HttpMediaType range in mediaRanges)
        {
            if (range.Includes(contentType))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Attempts to parse <paramref name="input"/> as an <c>Accept-Query</c> field value.
    /// </summary>
    /// <param name="input">The field text (e.g. <c>"application/jsonpath", application/sql;charset="UTF-8"</c>).</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed media ranges.</param>
    /// <returns>
    /// <see langword="true"/> when the value is a well-formed structured-field list of media ranges;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<char> input, out HttpAcceptQuery result)
    {
        result = default;

        if (!StructuredFieldList.TryParse(input, out StructuredFieldList list))
        {
            return false;
        }

        if (list.Count == 0)
        {
            result = Empty;
            return true;
        }

        var ranges = new HttpMediaType[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            if (!TryProjectMediaRange(list[i], out HttpMediaType mediaRange))
            {
                return false;
            }
            ranges[i] = mediaRange;
        }

        result = new HttpAcceptQuery(ranges);
        return true;
    }

    /// <summary>
    /// Attempts to parse the combined value of a possibly multi-line <c>Accept-Query</c> header field.
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined by comma (RFC 9651 &#167; 4.2).</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed media ranges.</param>
    /// <returns><see langword="true"/> when the value is a well-formed <c>Accept-Query</c> field.</returns>
    public static bool TryParse(HttpHeaderValue value, out HttpAcceptQuery result)
        => TryParse(value.Value.AsSpan(), out result);

    /// <summary>
    /// Parses <paramref name="input"/> as an <c>Accept-Query</c> field value.
    /// </summary>
    /// <param name="input">The field text.</param>
    /// <returns>The parsed <see cref="HttpAcceptQuery"/>.</returns>
    /// <exception cref="HttpException">The value is not a well-formed <c>Accept-Query</c> field.</exception>
    public static HttpAcceptQuery Parse(ReadOnlySpan<char> input)
    {
        if (!TryParse(input, out HttpAcceptQuery result))
        {
            throw new HttpInvalidStructuredFieldException($"The value is not a valid Accept-Query field: '{input.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Parses the combined value of a possibly multi-line <c>Accept-Query</c> header field.
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined per RFC 9651 &#167; 4.2.</param>
    /// <returns>The parsed <see cref="HttpAcceptQuery"/>.</returns>
    /// <exception cref="HttpException">The value is not a well-formed <c>Accept-Query</c> field.</exception>
    public static HttpAcceptQuery Parse(HttpHeaderValue value) => Parse(value.Value.AsSpan());

    private static bool TryProjectMediaRange(StructuredFieldMember member, out HttpMediaType mediaRange)
    {
        mediaRange = default;

        // A media range is a single item (a Token or String bare value plus its media-type
        // parameters); an inner list has no media-range meaning in Accept-Query.
        if (member.IsInnerList)
        {
            return false;
        }

        StructuredFieldItem item = member.Item;
        string? rangeText = item.Value.Type switch
        {
            StructuredFieldType.Token => item.Value.AsToken(),
            StructuredFieldType.String => item.Value.AsString(),
            _ => null,
        };
        if (rangeText is null)
        {
            return false;
        }

        // Fold the structured-field parameters back onto the media range in their canonical
        // textual form (e.g. ;charset="UTF-8") and reuse the media-type parser so the result
        // shares one representation with Accept / Content-Type.
        string mediaTypeText = item.Parameters.Count == 0
            ? rangeText
            : rangeText + item.Parameters.Serialize();

        return HttpMediaType.TryParse(mediaTypeText, out mediaRange);
    }

    /// <summary>
    /// Serializes this value to its canonical RFC 10008 &#167; 3 <c>Accept-Query</c> form: an RFC 9651
    /// list of media ranges, each emitted as a Token when it matches the <c>sf-token</c> grammar and
    /// otherwise as a String, with media-type parameters as structured-field parameters. Returns the
    /// empty string when no media ranges are advertised.
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    /// <exception cref="HttpException">A media range or parameter cannot be encoded as a structured field.</exception>
    public string Serialize()
    {
        if (mediaRanges is null)
        {
            return string.Empty;
        }

        var members = new StructuredFieldMember[mediaRanges.Length];
        for (int i = 0; i < mediaRanges.Length; i++)
        {
            members[i] = StructuredFieldMember.FromItem(ToStructuredFieldItem(mediaRanges[i]));
        }

        return new StructuredFieldList(members).Serialize();
    }

    private static StructuredFieldItem ToStructuredFieldItem(HttpMediaType mediaRange)
    {
        string rangeText = $"{mediaRange.Type}/{mediaRange.SubType}";

        StructuredFieldBareItem bareItem;
        try
        {
            bareItem = StructuredFieldGrammar.IsValidToken(rangeText)
                ? StructuredFieldBareItem.FromToken(rangeText)
                : StructuredFieldBareItem.FromString(rangeText);
        }
        catch (ArgumentException)
        {
            throw new HttpInvalidStructuredFieldException($"The media range '{rangeText}' cannot be encoded as an Accept-Query member.");
        }

        IReadOnlyList<HttpMediaTypeParameter> parameters = mediaRange.Parameters;
        if (parameters.Count == 0)
        {
            return new StructuredFieldItem(bareItem);
        }

        var pairs = new KeyValuePair<string, StructuredFieldBareItem>[parameters.Count];
        for (int i = 0; i < parameters.Count; i++)
        {
            HttpMediaTypeParameter parameter = parameters[i];
            if (!StructuredFieldGrammar.IsValidKey(parameter.Name))
            {
                throw new HttpInvalidStructuredFieldException($"The media-type parameter name '{parameter.Name}' is not a valid Accept-Query structured-field key.");
            }
            pairs[i] = new KeyValuePair<string, StructuredFieldBareItem>(parameter.Name, StructuredFieldBareItem.FromString(parameter.Value));
        }

        return new StructuredFieldItem(bareItem, new StructuredFieldParameters(pairs));
    }

    /// <inheritdoc />
    public bool Equals(HttpAcceptQuery other)
    {
        int count = Count;
        if (count != other.Count)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!mediaRanges![i].Equals(other.mediaRanges![i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpAcceptQuery other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (mediaRanges is null)
        {
            return 0;
        }
        var hash = new HashCode();
        foreach (HttpMediaType range in mediaRanges)
        {
            hash.Add(range);
        }
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Serialize();

    /// <summary>Determines whether two <c>Accept-Query</c> values are equal.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> if the values are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(HttpAcceptQuery left, HttpAcceptQuery right) => left.Equals(right);

    /// <summary>Determines whether two <c>Accept-Query</c> values are unequal.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> if the values are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(HttpAcceptQuery left, HttpAcceptQuery right) => !left.Equals(right);
}
