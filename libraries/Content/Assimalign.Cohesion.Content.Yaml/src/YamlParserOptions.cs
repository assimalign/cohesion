using System;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>Resource limits applied to YAML parsing and the parse-event pipeline.</summary>
public sealed class YamlParserOptions
{
    internal static YamlParserOptions Default { get; } = new();

    /// <summary>Gets the maximum number of nested collections, including the root. The default is 512.</summary>
    /// <remarks>Must be positive. Available execution stack can impose a lower limit on recursive parsing.</remarks>
    public int MaxDepth { get; init; } = 512;

    /// <summary>Gets the maximum expanded node count charged to aliases per document. The default is 1,000,000.</summary>
    /// <remarks>
    /// Must be nonnegative; zero forbids aliases. Each alias costs the expanded size of its target,
    /// including collection and mapping-key nodes. A reference to an open ancestor costs one and
    /// remains a cyclic reference, never recursively expanded. The model retains shared instances.
    /// </remarks>
    public long MaxAliasExpansion { get; init; } = 1_000_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxAliasExpansion);
    }
}
