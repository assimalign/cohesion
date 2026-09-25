using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single concrete byte slice selected from a representation by <see cref="HttpRangeSelector"/> —
/// a zero-based <see cref="Offset"/>/<see cref="Length"/> pair together with the
/// <see cref="HttpContentRange"/> a <c>206</c> response advertises for it (in a multipart
/// <c>206</c> this is the part's <c>Content-Range</c> header).
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpRangeSlice : IEquatable<HttpRangeSlice>
{
    /// <summary>
    /// Initializes a slice covering <paramref name="length"/> bytes at <paramref name="offset"/>
    /// within a representation of <paramref name="completeLength"/> bytes.
    /// </summary>
    /// <param name="offset">The zero-based offset of the first selected byte.</param>
    /// <param name="length">The number of selected bytes (must be at least one).</param>
    /// <param name="completeLength">The total representation length.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, <paramref name="length"/> is less than one, or the slice extends past <paramref name="completeLength"/>.</exception>
    public HttpRangeSlice(long offset, long length, long completeLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, completeLength);

        Offset = offset;
        Length = length;
        ContentRange = HttpContentRange.Satisfied(offset, offset + length - 1, completeLength);
    }

    /// <summary>Gets the zero-based offset of the first selected byte.</summary>
    public long Offset { get; }

    /// <summary>Gets the number of selected bytes.</summary>
    public long Length { get; }

    /// <summary>Gets the last selected byte position (inclusive).</summary>
    public long EndInclusive => Offset + Length - 1;

    /// <summary>Gets the <c>Content-Range</c> this slice advertises in a <c>206</c> response.</summary>
    public HttpContentRange ContentRange { get; }

    private string DebuggerDisplay => $"{Offset}..{EndInclusive} ({Length} bytes)";

    /// <inheritdoc />
    public bool Equals(HttpRangeSlice other) => Offset == other.Offset && Length == other.Length;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpRangeSlice other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Offset, Length);

    /// <summary>Determines whether two slices cover the same bytes.</summary>
    public static bool operator ==(HttpRangeSlice left, HttpRangeSlice right) => left.Equals(right);

    /// <summary>Determines whether two slices cover different bytes.</summary>
    public static bool operator !=(HttpRangeSlice left, HttpRangeSlice right) => !left.Equals(right);
}
