using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Strong emphasis (conventionally rendered bold): a container of inline content.
/// </summary>
public sealed class MarkdownStrong : MarkdownInline
{
    /// <summary>Gets the strongly emphasized inline content, in order.</summary>
    public IList<MarkdownInline> Inlines { get; } = new List<MarkdownInline>();
}
