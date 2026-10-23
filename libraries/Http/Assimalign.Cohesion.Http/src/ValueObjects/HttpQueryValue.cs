using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the ordered values of a parsed HTTP query or form key.
/// </summary>
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[DebuggerDisplay("{Value}")]
public readonly struct HttpQueryValue : IReadOnlyList<string>, IEquatable<HttpQueryValue>
{
    // Reuse immutable, amortized append storage without the header comma-join semantics.
    private readonly HttpHeaderValue _values;

    /// <summary>
    /// Initializes a new query value.
    /// </summary>
    /// <param name="value">The raw query value.</param>
    public HttpQueryValue(string value)
    {
        _values = new HttpHeaderValue(value ?? string.Empty);
    }

    private HttpQueryValue(HttpHeaderValue values)
    {
        _values = values;
    }

    /// <summary>
    /// Gets the first value, or an empty string when there are no values.
    /// </summary>
    /// <remarks>Repeated values are never comma-joined. Enumerate this value to read every occurrence.</remarks>
    public string Value => Count == 0 ? string.Empty : this[0];

    /// <summary>
    /// Gets the number of values, including empty occurrences.
    /// </summary>
    public int Count => _values.Count;

    /// <summary>
    /// Gets a value in arrival order.
    /// </summary>
    /// <param name="index">The zero-based occurrence index.</param>
    /// <returns>The value at the specified index.</returns>
    /// <exception cref="IndexOutOfRangeException">The index is outside the value list.</exception>
    public string this[int index] => _values[index]!;

    /// <summary>
    /// Appends all values of the second list to the first without changing either list.
    /// </summary>
    /// <param name="first">The initial values.</param>
    /// <param name="second">The values to append.</param>
    /// <returns>The combined values in order.</returns>
    public static HttpQueryValue Concat(HttpQueryValue first, HttpQueryValue second) =>
        new(HttpHeaderValue.Concat(first._values, second._values));

    /// <summary>
    /// Enumerates every value in arrival order, including empty occurrences.
    /// </summary>
    /// <returns>An enumerator over the values.</returns>
    public IEnumerator<string> GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Gets a query value containing no occurrences.
    /// </summary>
    public static HttpQueryValue Empty => default;

    /// <summary>Parses the first value as <see cref="short"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public short GetInt16() => short.Parse(Value, CultureInfo.InvariantCulture);
    /// <summary>Parses the first value as <see cref="int"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public int GetInt32() => int.Parse(Value, CultureInfo.InvariantCulture);
    /// <summary>Parses the first value as <see cref="long"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public long GetInt64() => long.Parse(Value, CultureInfo.InvariantCulture);
    /// <summary>Parses the first value as <see cref="decimal"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public decimal GetDecimal() => decimal.Parse(Value, CultureInfo.InvariantCulture);
    /// <summary>Parses the first value as <see cref="double"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public double GetDouble() => double.Parse(Value, CultureInfo.InvariantCulture);
    /// <summary>Parses the first value as <see cref="float"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public float GetFloat() => float.Parse(Value, CultureInfo.InvariantCulture);
    /// <summary>Parses the first value as <see cref="bool"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public bool GetBoolean() => bool.Parse(Value);
    /// <summary>Parses the first value as <see cref="DateOnly"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public DateOnly GetDate() => DateOnly.Parse(Value, CultureInfo.InvariantCulture);
    /// <summary>Parses the first value as <see cref="DateTime"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public DateTime GetDateTime() => DateTime.Parse(Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    /// <summary>Parses the first value as <see cref="DateTimeOffset"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public DateTimeOffset GetDateTimeOffset() => DateTimeOffset.Parse(Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    /// <summary>Parses the first value as <see cref="TimeOnly"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public TimeOnly GetTime() => TimeOnly.Parse(Value, CultureInfo.InvariantCulture);
    /// <summary>Parses the first value as <see cref="TimeSpan"/>.</summary>
    /// <returns>The parsed first value.</returns>
    public TimeSpan GetTimeSpan() => TimeSpan.Parse(Value, CultureInfo.InvariantCulture);

    /// <summary>Compares every value ordinally and in order.</summary>
    /// <param name="other">The values to compare.</param>
    /// <returns>Whether all occurrences match.</returns>
    public bool Equals(HttpQueryValue other) => _values.Equals(other._values);
    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is HttpQueryValue value && Equals(value);
    /// <inheritdoc />
    public override string ToString() => Value;
    /// <inheritdoc />
    public override int GetHashCode() => _values.GetHashCode();
    /// <summary>Creates one occurrence from a string.</summary>
    /// <param name="value">The query value.</param>
    /// <returns>A single-value list.</returns>
    public static implicit operator HttpQueryValue(string value) => new(value);
    /// <summary>Reads the first occurrence as a string.</summary>
    /// <param name="value">The query values.</param>
    /// <returns>The first value, or an empty string when absent.</returns>
    public static implicit operator string(HttpQueryValue value) => value.Value;
    /// <summary>Determines whether two ordered value lists are equal.</summary>
    /// <param name="left">The first list.</param>
    /// <param name="right">The second list.</param>
    /// <returns>Whether the lists are equal.</returns>
    public static bool operator ==(HttpQueryValue left, HttpQueryValue right) => left.Equals(right);
    /// <summary>Determines whether two ordered value lists are different.</summary>
    /// <param name="left">The first list.</param>
    /// <param name="right">The second list.</param>
    /// <returns>Whether the lists are different.</returns>
    public static bool operator !=(HttpQueryValue left, HttpQueryValue right) => !left.Equals(right);

    private sealed class DebugView
    {
        private readonly HttpQueryValue _target;

        public DebugView(HttpQueryValue value)
        {
            _target = value;
        }

        public string Value => _target.Value;
        public int Count => _target.Count;
    }
}
