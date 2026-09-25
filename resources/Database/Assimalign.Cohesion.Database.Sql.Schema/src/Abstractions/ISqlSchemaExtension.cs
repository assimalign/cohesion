namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a model-specific schema extension.</summary>
public interface ISqlSchemaExtension
{
    /// <summary>Gets the extension name.</summary>
    string Name { get; }

    /// <summary>Gets the canonical extension value.</summary>
    string Value { get; }
}
