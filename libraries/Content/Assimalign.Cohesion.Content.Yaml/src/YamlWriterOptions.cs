namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Options controlling how the YAML writer presents documents.
/// </summary>
public sealed class YamlWriterOptions
{
    internal static YamlWriterOptions Default { get; } = new();

    /// <summary>Gets or sets the positive number of spaces per block indentation level. The default is two.</summary>
    /// <remarks>Writer creation and direct writes reject zero or negative values before emitting content.</remarks>
    public int IndentSize { get; init; } = 2;
}
