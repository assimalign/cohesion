using System;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>Controls the amount of text buffered by a line reader.</summary>
public sealed class TextLineReaderOptions
{
    internal static TextLineReaderOptions Default { get; } = new();

    /// <summary>Gets the maximum line length in UTF-16 code units, excluding its terminator. The default is 1,048,576.</summary>
    /// <remarks>Must be positive. Excess input throws ContentFormatException with LimitExceeded before being appended to the buffer.</remarks>
    public int MaxLineLength { get; init; } = 1_048_576;

    internal void Validate() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxLineLength);
}
