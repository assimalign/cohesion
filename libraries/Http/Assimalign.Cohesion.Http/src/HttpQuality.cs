using System;
using System.Diagnostics;
using System.Globalization;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An RFC 9110 &#167; 12.4.2 quality value (<c>qvalue</c>): a weight from <c>0</c> to <c>1</c>
/// with at most three decimal places, used to rank preferences in the <c>Accept</c> family of
/// headers. A weight of <c>0</c> means "not acceptable".
/// </summary>
/// <remarks>
/// The weight is stored as an integer number of thousandths (0&#8211;1000) so equality and
/// ordering are exact and free of floating-point rounding error.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpQuality : IEquatable<HttpQuality>, IComparable<HttpQuality>
{
    private const int Scale = 1000;
    private readonly int perMille;

    private HttpQuality(int perMille) => this.perMille = perMille;

    /// <summary>The lowest weight, <c>0</c> (not acceptable).</summary>
    public static HttpQuality Zero { get; } = new(0);

    /// <summary>The highest weight, <c>1</c> (the default when no <c>q</c> is specified).</summary>
    public static HttpQuality One { get; } = new(Scale);

    /// <summary>
    /// Gets the weight as a fraction between <c>0.0</c> and <c>1.0</c>.
    /// </summary>
    public double Value => perMille / (double)Scale;

    /// <summary>
    /// Gets the weight as an integer number of thousandths (0&#8211;1000), suitable for exact comparison.
    /// </summary>
    public int PerMille => perMille;

    /// <summary>
    /// Gets a value indicating whether the weight is greater than zero (i.e. the item is acceptable).
    /// </summary>
    public bool IsAcceptable => perMille > 0;

    private string DebuggerDisplay => Value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates a quality value from an integer number of thousandths, clamped to 0&#8211;1000.
    /// </summary>
    /// <param name="perMille">The weight in thousandths.</param>
    /// <returns>The corresponding <see cref="HttpQuality"/>.</returns>
    public static HttpQuality FromPerMille(int perMille)
        => new(Math.Clamp(perMille, 0, Scale));

    /// <summary>
    /// Parses an RFC 9110 &#167; 12.4.2 <c>qvalue</c>: <c>0</c> or <c>1</c>, optionally followed by
    /// a decimal point and up to three digits (three zeros for <c>1</c>). Values outside
    /// <c>0</c>&#8211;<c>1</c> and over-long fractions are rejected.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="quality">When this method returns <see langword="true"/>, the parsed weight.</param>
    /// <returns><see langword="true"/> when the value is a well-formed <c>qvalue</c>.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpQuality quality)
    {
        quality = default;
        value = value.Trim();
        if (value.IsEmpty)
        {
            return false;
        }

        char first = value[0];
        if (first != '0' && first != '1')
        {
            return false;
        }

        if (value.Length == 1)
        {
            quality = first == '1' ? One : Zero;
            return true;
        }

        if (value[1] != '.')
        {
            return false;
        }

        ReadOnlySpan<char> fraction = value[2..];
        if (fraction.Length > 3)
        {
            return false;
        }

        int fractionValue = 0;
        int multiplier = 100;
        foreach (char c in fraction)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
            fractionValue += (c - '0') * multiplier;
            multiplier /= 10;
        }

        int total = (first - '0') * Scale + fractionValue;
        if (total > Scale)
        {
            // e.g. "1.001" — a weight above 1 is invalid.
            return false;
        }

        quality = new HttpQuality(total);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(HttpQuality other) => perMille == other.perMille;

    /// <inheritdoc />
    public int CompareTo(HttpQuality other) => perMille.CompareTo(other.perMille);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpQuality other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => perMille;

    /// <inheritdoc />
    public override string ToString() => DebuggerDisplay;

    /// <summary>Determines whether two weights are equal.</summary>
    public static bool operator ==(HttpQuality left, HttpQuality right) => left.perMille == right.perMille;

    /// <summary>Determines whether two weights are not equal.</summary>
    public static bool operator !=(HttpQuality left, HttpQuality right) => left.perMille != right.perMille;

    /// <summary>Determines whether the left weight is less than the right.</summary>
    public static bool operator <(HttpQuality left, HttpQuality right) => left.perMille < right.perMille;

    /// <summary>Determines whether the left weight is greater than the right.</summary>
    public static bool operator >(HttpQuality left, HttpQuality right) => left.perMille > right.perMille;

    /// <summary>Determines whether the left weight is less than or equal to the right.</summary>
    public static bool operator <=(HttpQuality left, HttpQuality right) => left.perMille <= right.perMille;

    /// <summary>Determines whether the left weight is greater than or equal to the right.</summary>
    public static bool operator >=(HttpQuality left, HttpQuality right) => left.perMille >= right.perMille;
}
