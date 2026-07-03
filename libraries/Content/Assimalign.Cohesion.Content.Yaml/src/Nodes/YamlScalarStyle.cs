namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// The presentation style of a scalar.
/// </summary>
public enum YamlScalarStyle
{
    /// <summary>An unquoted plain scalar.</summary>
    Plain,

    /// <summary>A single-quoted scalar.</summary>
    SingleQuoted,

    /// <summary>A double-quoted scalar.</summary>
    DoubleQuoted,

    /// <summary>A literal block scalar (<c>|</c>), preserving line breaks.</summary>
    Literal,

    /// <summary>A folded block scalar (<c>&gt;</c>), folding line breaks to spaces.</summary>
    Folded
}
