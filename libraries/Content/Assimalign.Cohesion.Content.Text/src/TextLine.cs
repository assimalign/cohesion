namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// A single line of text produced by <see cref="TextLineReader"/>: its content without the terminator,
/// its one-based line number, and the terminator that ended it.
/// </summary>
public readonly struct TextLine
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TextLine"/> structure.
    /// </summary>
    /// <param name="text">The line content without its terminator.</param>
    /// <param name="number">The one-based line number.</param>
    /// <param name="ending">The terminator that ended the line.</param>
    public TextLine(string text, int number, TextLineEnding ending)
    {
        Text = text;
        Number = number;
        Ending = ending;
    }

    /// <summary>Gets the line content without its terminator.</summary>
    public string Text { get; }

    /// <summary>Gets the one-based line number.</summary>
    public int Number { get; }

    /// <summary>Gets the terminator that ended the line.</summary>
    public TextLineEnding Ending { get; }
}
