using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// A YAML stream: the ordered documents carried by one piece of YAML text (the specification's term
/// for the top-level unit, unrelated to I/O streams).
/// </summary>
public sealed class YamlStream : IEnumerable<YamlDocument>
{
    /// <summary>Initializes an empty stream.</summary>
    public YamlStream()
    {
    }

    /// <summary>Initializes a stream with a single document.</summary>
    /// <param name="document">The document.</param>
    public YamlStream(YamlDocument document)
    {
        Documents.Add(document);
    }

    /// <summary>Gets the documents of the stream, in order.</summary>
    public IList<YamlDocument> Documents { get; } = new List<YamlDocument>();

    /// <summary>Gets the number of documents in the stream.</summary>
    public int Count => Documents.Count;

    /// <summary>Gets the document at the given index.</summary>
    /// <param name="index">The zero-based index.</param>
    public YamlDocument this[int index] => Documents[index];

    /// <inheritdoc/>
    public IEnumerator<YamlDocument> GetEnumerator() => Documents.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
