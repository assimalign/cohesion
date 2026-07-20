namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Base type for inline-level nodes: literal text, emphasis, strong emphasis, code spans, links,
/// images, and line breaks.
/// </summary>
public abstract class MarkdownInline : MarkdownNode
{
    private protected MarkdownInline()
    {
    }
}
