using System.Globalization;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Thrown when YAML content is malformed or violates the YAML 1.2 specification. Carries the one-based
/// line and column at which the problem was detected.
/// </summary>
public class YamlException : ContentFormatException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="YamlException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the malformed input.</param>
    /// <param name="line">The one-based line at which the problem was detected.</param>
    /// <param name="column">The one-based column at which the problem was detected.</param>
    public YamlException(string message, int line, int column)
        : this(message, ContentFormatErrorCode.InvalidStructure, line, column)
    {
    }

    /// <summary>Initializes a classified YAML failure with its source location.</summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="errorCode">The shared failure classification.</param>
    /// <param name="line">The one-based line at which the problem was detected.</param>
    /// <param name="column">The one-based UTF-16 column at which the problem was detected.</param>
    /// <param name="position">The absolute UTF-16 offset in the decoded source, when known.</param>
    public YamlException(string message, ContentFormatErrorCode errorCode, int line, int column, long? position = null)
        : base(string.Format(CultureInfo.CurrentCulture, ErrorMessages.MalformedInputLocation, message, line, column), errorCode, position)
    {
        Line = line;
        Column = column;
    }

    /// <summary>Gets the one-based line at which the problem was detected.</summary>
    public int Line { get; }

    /// <summary>Gets the one-based column at which the problem was detected.</summary>
    public int Column { get; }

    internal static YamlException At(string message, ContentFormatErrorCode code, int line, int column, string source)
    {
        // Only failures scan the original source. Do not allocate an offset table for every
        // character merely to undo the parser's BOM removal and CR/CRLF normalization.
        var offset = source.Length > 0 && source[0] == '\uFEFF' ? 1 : 0;
        for (var currentLine = 1; currentLine < line && offset < source.Length; currentLine++)
        {
            while (offset < source.Length && source[offset] is not ('\r' or '\n')) { offset++; }
            if (offset < source.Length && source[offset++] == '\r' && offset < source.Length && source[offset] == '\n') { offset++; }
        }
        return new YamlException(message, code, line, column, System.Math.Min(source.Length, offset + (long)column - 1));
    }
}
