using System;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>Controls resource limits that degrade excess Markdown syntax to text.</summary>
public sealed class MarkdownParserOptions
{
    internal static MarkdownParserOptions Default { get; } = new();

    /// <summary>Gets the maximum block container depth. The default is 128; the value must be positive.</summary>
    /// <remarks>The document is excluded; quotes, lists and list items each count one. Excess block starts become paragraph text.</remarks>
    public int MaxContainerDepth { get; init; } = 128;

    /// <summary>Gets the maximum parenthesis nesting inside a bare link destination. The default is 32; zero allows no nesting.</summary>
    /// <remarks>Must be nonnegative. A deeper destination is not recognized as a link; its source is parsed as ordinary inline text.</remarks>
    public int MaxLinkDestinationDepth { get; init; } = 32;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxContainerDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxLinkDestinationDepth);
    }
}
