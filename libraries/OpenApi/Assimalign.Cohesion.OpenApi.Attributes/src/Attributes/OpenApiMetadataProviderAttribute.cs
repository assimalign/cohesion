using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Advertises an <see cref="IOpenApiMetadataProvider"/> that this assembly contributes to the OpenApi
/// metadata of every compilation that references it. The source generator applies it for the provider it
/// emits; applying it by hand contributes a hand-written provider the same way.
/// </summary>
/// <remarks>
/// <para>
/// The source generator in a referencing compilation reads this attribute from each referenced
/// assembly's metadata at compile time and emits an internal <c>OpenApiMetadataRegistry</c> that
/// constructs every advertised provider directly. Composition therefore needs no runtime discovery or
/// reflection, and trimming keeps each provider because generated code references it.
/// </para>
/// <para>
/// <see cref="ProviderType"/> must be declared in the assembly that carries the attribute and must be a
/// public, non-abstract class that is not an open generic type, implements
/// <see cref="IOpenApiMetadataProvider"/>, and has a public parameterless constructor. The generator skips
/// a provider that does not qualify and reports warning <c>OPENAPIGEN0001</c>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class OpenApiMetadataProviderAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiMetadataProviderAttribute"/> class.
    /// </summary>
    /// <param name="providerType">The provider type this assembly contributes.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="providerType"/> is <see langword="null"/>.</exception>
    public OpenApiMetadataProviderAttribute(Type providerType)
    {
        ArgumentNullException.ThrowIfNull(providerType);
        ProviderType = providerType;
    }

    /// <summary>Gets the provider type this assembly contributes.</summary>
    public Type ProviderType { get; }
}
