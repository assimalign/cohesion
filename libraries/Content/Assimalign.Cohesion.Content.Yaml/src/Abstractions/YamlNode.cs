namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Base type for the YAML document model. Concrete node kinds are <see cref="YamlScalar"/>,
/// <see cref="YamlSequence"/>, and <see cref="YamlMapping"/>; the set is closed.
/// </summary>
/// <remarks>
/// Aliases are resolved during composition: an alias refers to the same node instance as its anchor,
/// so shared structure is represented by reference identity. The emitter re-introduces anchors and
/// aliases for nodes that occur more than once.
/// </remarks>
public abstract class YamlNode
{
    private protected YamlNode()
    {
    }

    /// <summary>Gets or sets the anchor name declared for this node, without the leading <c>&amp;</c>.</summary>
    public string? Anchor { get; set; }

    /// <summary>
    /// Gets or sets the node's resolved tag, for example <c>tag:yaml.org,2002:str</c> or an
    /// application-specific <c>!name</c> tag. <see langword="null"/> when no explicit tag applies
    /// beyond core-schema resolution.
    /// </summary>
    public string? Tag { get; set; }
}
