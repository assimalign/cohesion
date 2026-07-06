namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// The line terminator that ended a line of text.
/// </summary>
public enum TextLineEnding
{
    /// <summary>The line ended at the end of the text with no terminator.</summary>
    None,

    /// <summary>The line ended with a line feed (<c>\n</c>).</summary>
    LineFeed,

    /// <summary>The line ended with a carriage return and line feed pair (<c>\r\n</c>).</summary>
    CarriageReturnLineFeed,

    /// <summary>The line ended with a lone carriage return (<c>\r</c>).</summary>
    CarriageReturn
}
