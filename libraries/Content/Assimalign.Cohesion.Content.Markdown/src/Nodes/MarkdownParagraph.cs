using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// A paragraph: a sequence of inline content.
/// </summary>
public sealed class MarkdownParagraph : MarkdownBlock
{
    /// <summary>Gets the inline content of the paragraph, in order.</summary>
    public IList<MarkdownInline> Inlines { get; } = new List<MarkdownInline>();
}
