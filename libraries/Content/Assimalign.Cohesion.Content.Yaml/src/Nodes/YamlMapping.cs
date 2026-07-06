using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// A single key-value entry of a <see cref="YamlMapping"/>. YAML permits any node as a key.
/// </summary>
/// <param name="key">The entry key.</param>
/// <param name="value">The entry value.</param>
public readonly struct YamlMappingEntry(YamlNode key, YamlNode value)
{
    /// <summary>Gets the entry key.</summary>
    public YamlNode Key { get; } = key;

    /// <summary>Gets the entry value.</summary>
    public YamlNode Value { get; } = value;
}

/// <summary>
/// A mapping node: an ordered collection of key-value entries. Entry order is preserved for
/// round-trip fidelity; keys may be arbitrary nodes, with string-keyed convenience access for the
/// dominant case.
/// </summary>
public sealed class YamlMapping : YamlNode, IEnumerable<YamlMappingEntry>
{
    /// <summary>Gets the entries of the mapping, in authored order.</summary>
    public IList<YamlMappingEntry> Entries { get; } = new List<YamlMappingEntry>();

    /// <summary>Gets or sets the presentation style of the mapping.</summary>
    public YamlCollectionStyle Style { get; set; }

    /// <summary>Gets the number of entries in the mapping.</summary>
    public int Count => Entries.Count;

    /// <summary>
    /// Gets the value of the first entry whose key is a scalar equal to <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The scalar key text.</param>
    /// <exception cref="KeyNotFoundException">Thrown when no entry has the key.</exception>
    public YamlNode this[string key] =>
        TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The mapping contains no '{key}' key.");

    /// <summary>Adds an entry with a scalar key.</summary>
    /// <param name="key">The scalar key text.</param>
    /// <param name="value">The entry value.</param>
    public void Add(string key, YamlNode value) => Entries.Add(new YamlMappingEntry(YamlScalar.FromString(key), value));

    /// <summary>Adds an entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The entry value.</param>
    public void Add(YamlNode key, YamlNode value) => Entries.Add(new YamlMappingEntry(key, value));

    /// <summary>
    /// Finds the value of the first entry whose key is a scalar equal to <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The scalar key text.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the entry value.</param>
    /// <returns><see langword="true"/> when an entry with the key exists.</returns>
    public bool TryGetValue(string key, [NotNullWhen(true)] out YamlNode? value)
    {
        foreach (var entry in Entries)
        {
            if (entry.Key is YamlScalar scalar && scalar.Value == key)
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<YamlMappingEntry> GetEnumerator() => Entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
