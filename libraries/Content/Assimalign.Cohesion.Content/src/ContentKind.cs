namespace Assimalign.Cohesion.Content;

/// <summary>
/// The broad classification of a piece of content, used by <see cref="ContentFormat"/> to describe what
/// family of content a format belongs to.
/// </summary>
public enum ContentKind
{
    /// <summary>The kind of the content is not known.</summary>
    Unknown,

    /// <summary>Raw or structured binary content.</summary>
    Binary,

    /// <summary>Character-based text content.</summary>
    Text,

    /// <summary>Structured document content such as PDF, Markdown, YAML, or JSON.</summary>
    Document,

    /// <summary>Audio, video, or image media content.</summary>
    Media,

    /// <summary>Executable content such as native or managed binaries.</summary>
    Executable,

    /// <summary>Content composed of child content items, such as a container or multipart body.</summary>
    Composite
}
