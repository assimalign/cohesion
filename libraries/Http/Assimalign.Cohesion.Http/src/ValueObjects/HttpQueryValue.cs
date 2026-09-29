using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents a parsed HTTP query-string value.
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpQueryValue : IEquatable<HttpQueryValue>
{
    /// <summary>
    /// Initializes a new query value.
    /// </summary>
    /// <param name="value">The raw query value.</param>
    public HttpQueryValue(string value)
    {
        Value = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the raw query value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets an empty query value.
    /// </summary>
    public static HttpQueryValue Empty => new(string.Empty);

    public short GetInt16() => short.Parse(Value, CultureInfo.InvariantCulture);
    public int GetInt32() => int.Parse(Value, CultureInfo.InvariantCulture);
    public long GetInt64() => long.Parse(Value, CultureInfo.InvariantCulture);
    public decimal GetDecimal() => decimal.Parse(Value, CultureInfo.InvariantCulture);
    public double GetDouble() => double.Parse(Value, CultureInfo.InvariantCulture);
    public float GetFloat() => float.Parse(Value, CultureInfo.InvariantCulture);
    public bool GetBoolean() => bool.Parse(Value);
    public DateOnly GetDate() => DateOnly.Parse(Value, CultureInfo.InvariantCulture);
    public DateTime GetDateTime() => DateTime.Parse(Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public DateTimeOffset GetDateTimeOffset() => DateTimeOffset.Parse(Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public TimeOnly GetTime() => TimeOnly.Parse(Value, CultureInfo.InvariantCulture);
    public TimeSpan GetTimeSpan() => TimeSpan.Parse(Value, CultureInfo.InvariantCulture);

    public bool Equals(HttpQueryValue other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is HttpQueryValue value && Equals(value);
    public override string ToString() => Value;
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public static implicit operator HttpQueryValue(string value) => new(value);
    public static implicit operator string(HttpQueryValue value) => value.Value;
    public static bool operator ==(HttpQueryValue left, HttpQueryValue right) => left.Equals(right);
    public static bool operator !=(HttpQueryValue left, HttpQueryValue right) => !left.Equals(right);
}
