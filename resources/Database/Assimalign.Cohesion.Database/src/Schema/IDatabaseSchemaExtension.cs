namespace Assimalign.Cohesion.Database;

/// <summary>Describes a model-specific schema extension.</summary>
public interface IDatabaseSchemaExtension
{
    /// <summary>Gets the extension name.</summary>
    string Name { get; }

    /// <summary>Gets the canonical extension value.</summary>
    string Value { get; }
}
