using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// A validator for one value of the <c>format</c> keyword.
/// </summary>
/// <remarks>
/// A format constrains only the instance types it is defined for. Every other type is valid: a string
/// format such as <c>email</c> accepts numbers, objects and arrays.
/// </remarks>
public interface IJsonSchemaFormat
{
    /// <summary>
    /// Gets the format name, as written in the <c>format</c> keyword.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Determines whether an instance satisfies the format.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <returns>
    /// <see langword="true"/> when the instance conforms, or when its type is not one the format
    /// constrains.
    /// </returns>
    bool IsValid(JsonElement instance);
}
