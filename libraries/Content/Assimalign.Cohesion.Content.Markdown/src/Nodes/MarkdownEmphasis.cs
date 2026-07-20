using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Emphasis (conventionally rendered italic): a container of inline content.
/// </summary>
public sealed class MarkdownEmphasis : MarkdownInline
{
    /// <summary>Gets the emphasized inline content, in order.</summary>
    public IList<MarkdownInline> Inlines { get; } = new List<MarkdownInline>();
}
