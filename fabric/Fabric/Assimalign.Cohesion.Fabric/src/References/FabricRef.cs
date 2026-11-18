using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// A structural reference from one resource's spec to another resource in the same application,
/// written in JSON as <c>{ "$ref": "{Namespace}/{type}/{name}", "path": "outputs.url" }</c>.
/// </summary>
/// <remarks>
/// <para>
/// A reference is resolved by the ResourceManager at reconcile, from the target's status. It is
/// structural, never string interpolation, so the ResourceManager can order resources by their
/// references, show the edge in a what-if, and refuse a reference that does not resolve inside
/// a platform's view.
/// </para>
/// <para>
/// The document names the target by type and name; the ResourceManager stores the edge by the
/// target's <see cref="ResourceUid"/>, so a move between applications does not break it.
/// </para>
/// </remarks>
/// <param name="Type">The target's resource type.</param>
/// <param name="Name">The target's name inside the application.</param>
/// <param name="Path">The dotted path into the target, such as <c>outputs.connectionString</c>; <see langword="null"/> refers to the resource itself.</param>
[JsonConverter(typeof(FabricRefJsonConverter))]
public sealed record FabricRef(ResourceTypeName Type, ResourceName Name, string? Path = null)
{
    /// <summary>
    /// The JSON member that marks an object as a reference.
    /// </summary>
    public const string RefPropertyName = "$ref";

    /// <summary>
    /// The JSON member that holds the path into the target.
    /// </summary>
    public const string PathPropertyName = "path";

    /// <summary>
    /// Gets the reference target as written in JSON, <c>{Namespace}/{type}/{name}</c>.
    /// </summary>
    public string Target => $"{Type}/{Name}";

    /// <summary>
    /// Reads a reference from a JSON value, if the value is one.
    /// </summary>
    /// <param name="element">The JSON value.</param>
    /// <param name="reference">The reference, when <paramref name="element"/> is an object with a <c>$ref</c> member.</param>
    /// <returns><see langword="true"/> when <paramref name="element"/> is a reference; <see langword="false"/> for any other value, which is a literal.</returns>
    /// <exception cref="JsonException">Thrown when <paramref name="element"/> has a <c>$ref</c> member but is not a well-formed reference. Unknown members are refused, as everywhere in the document.</exception>
    public static bool TryRead(JsonElement element, [NotNullWhen(true)] out FabricRef? reference)
    {
        reference = null;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(RefPropertyName, out var target))
        {
            return false;
        }

        string? path = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(RefPropertyName))
            {
                continue;
            }

            if (property.NameEquals(PathPropertyName) && property.Value.ValueKind == JsonValueKind.String)
            {
                path = property.Value.GetString();
                continue;
            }

            throw new JsonException(ErrorMessages.FabricRefShapeInvalid);
        }

        if (target.ValueKind != JsonValueKind.String || !TryParseTarget(target.GetString(), out var type, out var name))
        {
            throw new JsonException(ErrorMessages.FabricRefShapeInvalid);
        }

        reference = new FabricRef(type, name, path);
        return true;
    }

    /// <summary>
    /// Writes this reference as its JSON object.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> is <see langword="null"/>.</exception>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString(RefPropertyName, Target);
        if (Path is not null)
        {
            writer.WriteString(PathPropertyName, Path);
        }

        writer.WriteEndObject();
    }

    private static bool TryParseTarget(string? value, out ResourceTypeName type, out ResourceName name)
    {
        type = default;
        name = default;
        var last = value is null ? -1 : value.LastIndexOf('/');
        return last > 0
            && ResourceTypeName.TryCreate(value![..last], out type)
            && ResourceName.TryCreate(value[(last + 1)..], out name);
    }
}
