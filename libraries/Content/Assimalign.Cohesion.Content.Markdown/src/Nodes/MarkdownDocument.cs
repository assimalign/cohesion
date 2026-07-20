using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// The root of a parsed Markdown document: an ordered collection of block-level nodes.
/// </summary>
public sealed class MarkdownDocument : MarkdownNode, IEnumerable<MarkdownBlock>
{
    /// <summary>Gets the top-level blocks of the document, in order.</summary>
    public IList<MarkdownBlock> Blocks { get; } = new List<MarkdownBlock>();

    /// <summary>Gets the number of top-level blocks.</summary>
    public int Count => Blocks.Count;

    /// <summary>Gets the block at the given index.</summary>
    /// <param name="index">The zero-based index.</param>
    public MarkdownBlock this[int index] => Blocks[index];

    /// <summary>Adds a block to the end of the document.</summary>
    /// <param name="block">The block to add.</param>
    public void Add(MarkdownBlock block) => Blocks.Add(block);

    /// <inheritdoc/>
    public IEnumerator<MarkdownBlock> GetEnumerator() => Blocks.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
