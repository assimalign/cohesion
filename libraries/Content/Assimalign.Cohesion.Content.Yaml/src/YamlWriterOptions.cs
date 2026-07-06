namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Options controlling how the YAML writer presents documents.
/// </summary>
public sealed class YamlWriterOptions
{
    internal static YamlWriterOptions Default { get; } = new();

    /// <summary>Gets or sets the number of spaces per block indentation level. The default is two.</summary>
    public int IndentSize { get; init; } = 2;
}
