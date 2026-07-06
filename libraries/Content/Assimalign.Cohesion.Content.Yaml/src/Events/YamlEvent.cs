namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Discriminates the kind of a <see cref="YamlEvent"/>.
/// </summary>
public enum YamlEventKind
{
    /// <summary>The start of the YAML stream.</summary>
    StreamStart,

    /// <summary>The end of the YAML stream.</summary>
    StreamEnd,

    /// <summary>The start of a document.</summary>
    DocumentStart,

    /// <summary>The end of a document.</summary>
    DocumentEnd,

    /// <summary>A scalar value.</summary>
    Scalar,

    /// <summary>An alias referring to an anchored node.</summary>
    Alias,

    /// <summary>The start of a sequence.</summary>
    SequenceStart,

    /// <summary>The end of a sequence.</summary>
    SequenceEnd,

    /// <summary>The start of a mapping.</summary>
    MappingStart,

    /// <summary>The end of a mapping.</summary>
    MappingEnd
}

/// <summary>
/// A single event of the YAML parse-event pipeline: the boundary and content events a parser produces
/// and a composer or streaming consumer processes.
/// </summary>
public readonly struct YamlEvent
{
    internal YamlEvent(
        YamlEventKind kind,
        int line,
        int column,
        string? value = null,
        string? anchor = null,
        string? tag = null,
        YamlScalarStyle scalarStyle = YamlScalarStyle.Plain,
        YamlCollectionStyle collectionStyle = YamlCollectionStyle.Block,
        bool isExplicit = false)
    {
        Kind = kind;
        Line = line;
        Column = column;
        Value = value;
        Anchor = anchor;
        Tag = tag;
        ScalarStyle = scalarStyle;
        CollectionStyle = collectionStyle;
        IsExplicit = isExplicit;
    }

    /// <summary>Gets the kind of the event.</summary>
    public YamlEventKind Kind { get; }

    /// <summary>Gets the one-based line at which the event begins.</summary>
    public int Line { get; }

    /// <summary>Gets the one-based column at which the event begins.</summary>
    public int Column { get; }

    /// <summary>Gets the scalar content for <see cref="YamlEventKind.Scalar"/> events, or the alias target for <see cref="YamlEventKind.Alias"/> events.</summary>
    public string? Value { get; }

    /// <summary>Gets the anchor declared on the node, without the leading <c>&amp;</c>.</summary>
    public string? Anchor { get; }

    /// <summary>Gets the resolved tag declared on the node.</summary>
    public string? Tag { get; }

    /// <summary>Gets the scalar presentation style for <see cref="YamlEventKind.Scalar"/> events.</summary>
    public YamlScalarStyle ScalarStyle { get; }

    /// <summary>Gets the collection presentation style for collection-start events.</summary>
    public YamlCollectionStyle CollectionStyle { get; }

    /// <summary>Gets a value indicating whether a document boundary was written explicitly (<c>---</c> / <c>...</c>).</summary>
    public bool IsExplicit { get; }
}
