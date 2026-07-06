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
        : base($"{message} (line {line}, column {column})")
    {
        Line = line;
        Column = column;
    }

    /// <summary>Gets the one-based line at which the problem was detected.</summary>
    public int Line { get; }

    /// <summary>Gets the one-based column at which the problem was detected.</summary>
    public int Column { get; }
}
