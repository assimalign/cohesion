using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The effective HTTP response-priority signal defined by RFC 9218 (Extensible
/// Prioritization Scheme for HTTP): an <see cref="Urgency"/> in the range 0..7 and
/// an <see cref="Incremental"/> flag.
/// </summary>
/// <remarks>
/// <para>
/// On the wire the signal appears in two places, both of which carry the same
/// <c>u</c>/<c>i</c> members of an RFC 9651 structured-field dictionary (the
/// <em>Priority Field Value</em>): the <c>Priority</c> request-header field
/// (RFC 9218 &#167; 4) and the HTTP/2 / HTTP/3 <c>PRIORITY_UPDATE</c> frame
/// (RFC 9218 &#167; 7). This value object is the parsed, validated projection of
/// that dictionary onto the two fields a scheduler actually consumes; it is
/// produced through the shared <see cref="StructuredFieldDictionary"/> toolkit so
/// there is a single structured-field parser in the stack.
/// </para>
/// <para>
/// Parsing is deliberately tolerant (RFC 9218 &#167; 4): an absent, out-of-range,
/// or wrong-typed <c>u</c> falls back to the default urgency of 3, an absent or
/// wrong-typed <c>i</c> falls back to non-incremental, and unrecognized members
/// are ignored. A malformed dictionary as a whole is signalled by a
/// <see langword="false"/> result from the <c>TryParse</c> overloads so the caller
/// can substitute <see cref="Default"/> without raising a protocol error.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct HttpPriority : IEquatable<HttpPriority>
{
    /// <summary>The default urgency (3) applied when <c>u</c> is absent or invalid (RFC 9218 &#167; 4.1).</summary>
    public const int DefaultUrgency = 3;

    /// <summary>The lowest urgency value, 0 — the most urgent (RFC 9218 &#167; 4.1).</summary>
    public const int MinUrgency = 0;

    /// <summary>The highest urgency value, 7 — the least urgent (RFC 9218 &#167; 4.1).</summary>
    public const int MaxUrgency = 7;

    /// <summary>The <c>u</c> member key of the Priority Field Value (RFC 9218 &#167; 4.1).</summary>
    private const string UrgencyKey = "u";

    /// <summary>The <c>i</c> member key of the Priority Field Value (RFC 9218 &#167; 4.2).</summary>
    private const string IncrementalKey = "i";

    /// <summary>
    /// Initializes a priority from an explicit urgency and incremental flag.
    /// </summary>
    /// <param name="urgency">The urgency; must be within 0..7 (RFC 9218 &#167; 4.1).</param>
    /// <param name="incremental">Whether the response can be processed incrementally (RFC 9218 &#167; 4.2).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="urgency"/> is outside 0..7.</exception>
    public HttpPriority(int urgency, bool incremental)
    {
        if (urgency < MinUrgency || urgency > MaxUrgency)
        {
            throw new ArgumentOutOfRangeException(nameof(urgency), urgency, "Urgency must be within the RFC 9218 range of 0..7.");
        }

        Urgency = urgency;
        Incremental = incremental;
    }

    /// <summary>
    /// Gets the urgency, 0 (most urgent) through 7 (least urgent). A numerically
    /// lower urgency is scheduled ahead of a higher one (RFC 9218 &#167; 4.1, &#167; 10).
    /// </summary>
    public int Urgency { get; }

    /// <summary>
    /// Gets a value indicating whether the response can be processed incrementally —
    /// interleaved round-robin with other same-urgency incremental responses rather
    /// than sent to completion first (RFC 9218 &#167; 4.2, &#167; 10).
    /// </summary>
    public bool Incremental { get; }

    /// <summary>
    /// Gets the RFC 9218 default priority: urgency <see cref="DefaultUrgency"/> (3),
    /// non-incremental. This is the effective priority of a request that carries no
    /// <c>Priority</c> header and is not the subject of a <c>PRIORITY_UPDATE</c>.
    /// </summary>
    public static HttpPriority Default => new(DefaultUrgency, false);

    /// <summary>
    /// Projects an already-parsed structured-field dictionary onto a priority,
    /// applying the RFC 9218 &#167; 4 tolerance rules: an absent, non-integer, or
    /// out-of-range <c>u</c> yields the default urgency; an absent or non-boolean
    /// <c>i</c> yields non-incremental; every other member is ignored. This method
    /// always succeeds.
    /// </summary>
    /// <param name="dictionary">The parsed Priority Field Value dictionary.</param>
    /// <returns>The effective priority.</returns>
    public static HttpPriority FromDictionary(StructuredFieldDictionary dictionary)
    {
        int urgency = DefaultUrgency;
        if (dictionary.TryGetValue(UrgencyKey, out StructuredFieldMember urgencyMember)
            && !urgencyMember.IsInnerList
            && urgencyMember.Item.Value.Type == StructuredFieldType.Integer)
        {
            long candidate = urgencyMember.Item.Value.AsInteger();
            if (candidate >= MinUrgency && candidate <= MaxUrgency)
            {
                urgency = (int)candidate;
            }
        }

        bool incremental = false;
        if (dictionary.TryGetValue(IncrementalKey, out StructuredFieldMember incrementalMember)
            && !incrementalMember.IsInnerList
            && incrementalMember.Item.Value.Type == StructuredFieldType.Boolean)
        {
            incremental = incrementalMember.Item.Value.AsBoolean();
        }

        return new HttpPriority(urgency, incremental);
    }

    /// <summary>
    /// Parses a Priority Field Value (RFC 9218 &#167; 4 / &#167; 7) — the <c>u</c>/<c>i</c>
    /// structured-field dictionary carried by the <c>Priority</c> header or a
    /// <c>PRIORITY_UPDATE</c> frame.
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the effective priority
    /// (with RFC 9218 defaults filled in for absent or invalid members).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="input"/> is a well-formed structured-field
    /// dictionary; <see langword="false"/> when it cannot be parsed at all, in which case the
    /// caller substitutes <see cref="Default"/> per RFC 9218 &#167; 4.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<char> input, out HttpPriority result)
    {
        if (StructuredFieldDictionary.TryParse(input, out StructuredFieldDictionary dictionary))
        {
            result = FromDictionary(dictionary);
            return true;
        }

        result = Default;
        return false;
    }

    /// <summary>
    /// Parses the combined value of a possibly multi-line <c>Priority</c> header field
    /// as a Priority Field Value (RFC 9218 &#167; 4).
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined per RFC 9651 &#167; 4.2.</param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the effective priority.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the header value is a well-formed structured-field dictionary;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryParse(HttpHeaderValue value, out HttpPriority result)
        => TryParse(value.Value.AsSpan(), out result);

    /// <summary>
    /// Serializes this priority to its RFC 9218 canonical Priority Field Value.
    /// Members equal to their default (urgency 3, non-incremental) are omitted, so the
    /// default priority serializes to the empty string (RFC 9218 &#167; 4).
    /// </summary>
    /// <returns>The canonical textual representation.</returns>
    public string Serialize()
    {
        List<KeyValuePair<string, StructuredFieldMember>> members = new(2);

        if (Urgency != DefaultUrgency)
        {
            members.Add(new KeyValuePair<string, StructuredFieldMember>(
                UrgencyKey,
                StructuredFieldMember.FromItem(new StructuredFieldItem(StructuredFieldBareItem.FromInteger(Urgency)))));
        }

        if (Incremental)
        {
            members.Add(new KeyValuePair<string, StructuredFieldMember>(
                IncrementalKey,
                StructuredFieldMember.FromItem(new StructuredFieldItem(StructuredFieldBareItem.FromBoolean(true)))));
        }

        return new StructuredFieldDictionary(members).Serialize();
    }

    /// <inheritdoc />
    public bool Equals(HttpPriority other) => Urgency == other.Urgency && Incremental == other.Incremental;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpPriority other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Urgency, Incremental);

    /// <inheritdoc />
    public override string ToString() => Serialize();

    /// <summary>Determines whether two priorities are equal.</summary>
    /// <param name="left">The first priority.</param>
    /// <param name="right">The second priority.</param>
    /// <returns><see langword="true"/> if the priorities are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(HttpPriority left, HttpPriority right) => left.Equals(right);

    /// <summary>Determines whether two priorities are unequal.</summary>
    /// <param name="left">The first priority.</param>
    /// <param name="right">The second priority.</param>
    /// <returns><see langword="true"/> if the priorities are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(HttpPriority left, HttpPriority right) => !left.Equals(right);
}
