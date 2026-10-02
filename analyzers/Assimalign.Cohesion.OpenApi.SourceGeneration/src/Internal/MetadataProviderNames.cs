using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration.Internal;

/// <summary>
/// The names behind the cross-assembly provider seam: the public contract in
/// <c>Assimalign.Cohesion.OpenApi.Attributes</c>, and the provider type the generator emits for an
/// annotated assembly. That type's name is derived from the assembly name by an injective escape, so no
/// two assemblies in one compilation can emit the same provider type and a referencing compilation never
/// sees a CS0433 or CS0436 conflict between them.
/// </summary>
internal static class MetadataProviderNames
{
    /// <summary>The namespace of every generated type.</summary>
    internal const string Namespace = "Assimalign.Cohesion.OpenApi.Generated";

    /// <summary>The assembly that defines the provider contract and carries the generator.</summary>
    internal const string AttributesAssemblyName = "Assimalign.Cohesion.OpenApi.Attributes";

    /// <summary>The metadata name of the assembly-level attribute that advertises a provider.</summary>
    internal const string AttributeMetadataName = AttributesAssemblyName + ".OpenApiMetadataProviderAttribute";

    /// <summary>The metadata name of the interface every provider implements.</summary>
    internal const string InterfaceMetadataName = AttributesAssemblyName + ".IOpenApiMetadataProvider";

    private const string providerPrefix = "OpenApiMetadataProvider_";

    /// <summary>
    /// Builds the provider type name for an assembly. ASCII letters and digits are kept, a dot becomes
    /// two underscores, and any other character becomes <c>_x</c> followed by its four-digit hexadecimal
    /// code. Every underscore after the prefix therefore starts one of those two escapes, which makes the
    /// mapping injective: <c>Contoso.Pets</c> becomes <c>OpenApiMetadataProvider_Contoso__Pets</c> and
    /// <c>Contoso_Pets</c> becomes <c>OpenApiMetadataProvider_Contoso_x005FPets</c>.
    /// </summary>
    /// <param name="assemblyName">The simple name of the assembly the provider describes.</param>
    /// <returns>A valid C# identifier unique to <paramref name="assemblyName"/>.</returns>
    internal static string TypeName(string assemblyName)
    {
        var builder = new StringBuilder(providerPrefix.Length + assemblyName.Length);
        builder.Append(providerPrefix);
        foreach (var character in assemblyName)
        {
            if (character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(character);
            }
            else if (character == '.')
            {
                builder.Append("__");
            }
            else
            {
                builder.Append("_x").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
