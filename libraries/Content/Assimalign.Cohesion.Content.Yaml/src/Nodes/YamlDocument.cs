namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// A single YAML document: one root node, optionally introduced by directives and document markers.
/// </summary>
public sealed class YamlDocument
{
    /// <summary>Initializes an empty document.</summary>
    public YamlDocument()
    {
    }

    /// <summary>Initializes a document with a root node.</summary>
    /// <param name="root">The root node.</param>
    public YamlDocument(YamlNode root)
    {
        Root = root;
    }

    /// <summary>Gets or sets the root node of the document, or <see langword="null"/> for an empty document.</summary>
    public YamlNode? Root { get; set; }

    /// <summary>Gets or sets a value indicating whether the document was (or should be) introduced with an explicit <c>---</c> marker.</summary>
    public bool IsExplicit { get; set; }
}
