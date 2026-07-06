using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// A sequence node: an ordered collection of nodes.
/// </summary>
public sealed class YamlSequence : YamlNode, IEnumerable<YamlNode>
{
    /// <summary>Gets the items of the sequence, in order.</summary>
    public IList<YamlNode> Items { get; } = new List<YamlNode>();

    /// <summary>Gets or sets the presentation style of the sequence.</summary>
    public YamlCollectionStyle Style { get; set; }

    /// <summary>Gets the number of items in the sequence.</summary>
    public int Count => Items.Count;

    /// <summary>Gets the item at the given index.</summary>
    /// <param name="index">The zero-based index.</param>
    public YamlNode this[int index] => Items[index];

    /// <summary>Adds an item to the end of the sequence.</summary>
    /// <param name="item">The node to add.</param>
    public void Add(YamlNode item) => Items.Add(item);

    /// <inheritdoc/>
    public IEnumerator<YamlNode> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
