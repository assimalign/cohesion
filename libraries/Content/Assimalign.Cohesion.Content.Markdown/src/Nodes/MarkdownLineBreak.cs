namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// A line break inside inline content: hard (a rendered break) or soft (a source line ending that
/// renders as whitespace).
/// </summary>
public sealed class MarkdownLineBreak : MarkdownInline
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownLineBreak"/> class.
    /// </summary>
    /// <param name="isHard"><see langword="true"/> for a hard break; <see langword="false"/> for a soft break.</param>
    public MarkdownLineBreak(bool isHard)
    {
        IsHard = isHard;
    }

    /// <summary>Gets a value indicating whether the break is hard.</summary>
    public bool IsHard { get; }
}
