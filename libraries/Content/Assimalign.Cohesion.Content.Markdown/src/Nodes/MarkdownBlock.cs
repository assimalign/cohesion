namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Base type for block-level nodes: headings, paragraphs, block quotes, lists, code blocks, and
/// thematic breaks.
/// </summary>
public abstract class MarkdownBlock : MarkdownNode
{
    private protected MarkdownBlock()
    {
    }
}
