using System;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// A position within text: a one-based line number, a one-based character column, and a zero-based
/// character offset from the start of the text.
/// </summary>
/// <remarks>
/// Lines are physical: <c>\n</c>, <c>\r\n</c>, and lone <c>\r</c> each advance the line by one,
/// matching the package line model. Columns and offsets count UTF-16 code units — a tab is one
/// column, and characters outside the basic multilingual plane count two.
/// </remarks>
/// <param name="line">The one-based line number.</param>
/// <param name="column">The one-based character column within the line.</param>
/// <param name="offset">The zero-based character offset from the start of the text.</param>
public readonly struct TextPosition(int line, int column, long offset) : IEquatable<TextPosition>
{
    /// <summary>Gets the position of the first character of text: line one, column one, offset zero.</summary>
    public static TextPosition Start { get; } = new(1, 1, 0);

    /// <summary>Gets the one-based line number.</summary>
    public int Line { get; } = line;

    /// <summary>Gets the one-based character column within the line.</summary>
    public int Column { get; } = column;

    /// <summary>Gets the zero-based character offset from the start of the text.</summary>
    public long Offset { get; } = offset;

    /// <summary>
    /// Determines whether this position equals another.
    /// </summary>
    /// <param name="other">The position to compare with.</param>
    /// <returns><see langword="true"/> when line, column, and offset are all equal.</returns>
    public bool Equals(TextPosition other) => Line == other.Line && Column == other.Column && Offset == other.Offset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TextPosition other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Line, Column, Offset);

    /// <summary>
    /// Determines whether two positions are equal.
    /// </summary>
    /// <param name="left">The first position.</param>
    /// <param name="right">The second position.</param>
    /// <returns><see langword="true"/> when the positions are equal.</returns>
    public static bool operator ==(TextPosition left, TextPosition right) => left.Equals(right);

    /// <summary>
    /// Determines whether two positions are unequal.
    /// </summary>
    /// <param name="left">The first position.</param>
    /// <param name="right">The second position.</param>
    /// <returns><see langword="true"/> when the positions differ.</returns>
    public static bool operator !=(TextPosition left, TextPosition right) => !left.Equals(right);

    /// <summary>Returns the position as <c>(line,column)</c>.</summary>
    /// <returns>The formatted position.</returns>
    public override string ToString() => $"({Line},{Column})";
}
