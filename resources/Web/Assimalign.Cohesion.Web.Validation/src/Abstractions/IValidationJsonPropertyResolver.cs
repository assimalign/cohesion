using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.Web.Validation;

/// <summary>
/// Identifies a CLR member in a registered JSON contract without inspecting reflection metadata.
/// </summary>
/// <remarks>
/// The Web source generator implements this optional contract on source-declared partial
/// <c>JsonSerializerContext</c> types. A resolver that customizes property names beyond naming policies
/// and <c>JsonPropertyName</c> attributes can implement it itself. Validation reads it from the
/// registered contract's options resolver or originating resolver; no existing resolver interface changes.
/// </remarks>
public interface IValidationJsonPropertyResolver
{
    /// <summary>
    /// Gets the property corresponding to a CLR member in the supplied, final JSON contract.
    /// </summary>
    /// <param name="typeInfo">The application's registered JSON contract for the member's owner.</param>
    /// <param name="memberName">The CLR member name, without a parent path or collection index.</param>
    /// <returns>A property from <paramref name="typeInfo"/>, or <see langword="null"/> when none matches.</returns>
    JsonPropertyInfo? GetProperty(JsonTypeInfo typeInfo, string memberName);
}
