namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// The resolved kind of a scalar under the YAML 1.2 core schema. Quoted scalars always resolve to
/// <see cref="String"/>; plain scalars resolve by the core-schema patterns.
/// </summary>
public enum YamlScalarKind
{
    /// <summary>The null value (<c>null</c>, <c>~</c>, or an empty plain scalar).</summary>
    Null,

    /// <summary>A boolean value (<c>true</c>/<c>false</c> in any core-schema casing).</summary>
    Boolean,

    /// <summary>An integral number (decimal, <c>0o</c> octal, or <c>0x</c> hexadecimal).</summary>
    Integer,

    /// <summary>A floating-point number, including <c>.inf</c> and <c>.nan</c> forms.</summary>
    Float,

    /// <summary>A string value.</summary>
    String
}
