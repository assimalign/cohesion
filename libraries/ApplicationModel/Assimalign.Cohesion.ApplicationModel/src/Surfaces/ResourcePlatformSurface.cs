using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A surface a platform publishes for one resource: the manageable settings of a platform object, such as a
/// Kubernetes horizontal autoscaler, under a platform-qualified key, with the JSON schema its values conform to and
/// the value the application's code declares.
/// </summary>
/// <remarks>
/// <para>
/// A platform gateway creates one for each surface it realizes for a resource. The gateway management API serves it
/// at <c>platform/{platform}/{name}</c>: an operator's RFC 7396 merge patch is validated against <see cref="Schema"/>,
/// kept with the resource's deployment record until the resource's next deployment, and applied over
/// <see cref="Value"/> to give the value in force.
/// </para>
/// <para>
/// The schema is the subset the runtime surface uses (<see cref="ResourceSurfaceSection"/>): an object schema that
/// lists its members is closed, so an unknown member is refused rather than ignored.
/// </para>
/// </remarks>
[DebuggerDisplay("{Key}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ResourcePlatformSurface
{
    private readonly Func<JsonElement, string?>? _validate;

    private ResourcePlatformSurface(ResourceExtensionKey key, JsonElement schema, JsonElement value, Func<JsonElement, string?>? validate)
    {
        Key = key;
        Schema = schema;
        Value = value;
        _validate = validate;
    }

    /// <summary>Gets the platform-qualified key, such as <c>kubernetes/hpa</c>.</summary>
    public ResourceExtensionKey Key { get; }

    /// <summary>Gets the JSON schema every value of the surface conforms to.</summary>
    public JsonElement Schema { get; }

    /// <summary>Gets the value the application's code declares for the resource.</summary>
    public JsonElement Value { get; }

    /// <summary>Creates a surface from its key, its schema and the value code declares.</summary>
    /// <param name="key">The platform-qualified key.</param>
    /// <param name="schema">The JSON schema, as JSON text: an object or a boolean schema.</param>
    /// <param name="value">The value the application's code declares; it must conform to the schema.</param>
    /// <param name="validate">
    /// Checks a conforming value for rules the schema cannot state, such as one member bounding another, and returns
    /// why it is refused or <see langword="null"/>; or <see langword="null"/> for no further rule.
    /// </param>
    /// <returns>The surface; the schema and the value are copied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is the default value, <paramref name="schema"/> is not a schema, or
    /// <paramref name="value"/> is undefined, does not conform to the schema, or is refused by <paramref name="validate"/>.
    /// </exception>
    public static ResourcePlatformSurface Create(
        ResourceExtensionKey key,
        string schema,
        JsonElement value,
        Func<JsonElement, string?>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (key.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceExtensionKeyInvalid, nameof(key));
        }

        JsonElement parsed;
        try
        {
            using JsonDocument document = JsonDocument.Parse(schema);
            parsed = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(ErrorMessages.PlatformSurfaceSchemaInvalid, nameof(schema), exception);
        }

        if (parsed.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException(ErrorMessages.PlatformSurfaceSchemaInvalid, nameof(schema));
        }

        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(ErrorMessages.PlatformSurfaceValueInvalid, nameof(value));
        }

        if ((ResourceSurfaceSchemaValidator.ValidateValue(parsed, value) ?? validate?.Invoke(value)) is string error)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlatformSurfaceValueRejected, key, error),
                nameof(value));
        }

        return new ResourcePlatformSurface(key, parsed, value.Clone(), validate);
    }

    /// <summary>Validates an operator's merge patch, and the value it would put in force.</summary>
    /// <param name="patch">The RFC 7396 merge patch.</param>
    /// <param name="error">Why the patch is refused, naming the JSON path, when it is.</param>
    /// <returns>Whether the patch and its result conform to the schema.</returns>
    public bool TryValidatePatch(JsonElement patch, [NotNullWhen(false)] out string? error)
    {
        if (patch.ValueKind == JsonValueKind.Undefined)
        {
            error = ErrorMessages.PlatformSurfaceValueInvalid;
            return false;
        }

        error = ResourceSurfaceSchemaValidator.ValidatePatch(Schema, patch);
        if (error is null)
        {
            JsonElement result = Apply(patch);
            error = ResourceSurfaceSchemaValidator.ValidateValue(Schema, result) ?? _validate?.Invoke(result);
        }

        return error is null;
    }

    /// <summary>Returns the value in force with a management override applied over the declared value.</summary>
    /// <param name="patch">The override as an RFC 7396 merge patch, or <see langword="null"/> for none.</param>
    /// <returns>A new value; neither <see cref="Value"/> nor <paramref name="patch"/> is changed.</returns>
    public JsonElement Apply(JsonElement? patch)
    {
        if (patch is not JsonElement merge || merge.ValueKind == JsonValueKind.Undefined)
        {
            return Value.Clone();
        }

        JsonNode? result = JsonMergePatchComposition.Apply(JsonNode.Parse(Value.GetRawText()), JsonNode.Parse(merge.GetRawText()));
        using JsonDocument document = JsonDocument.Parse(result?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    private sealed class DebugView
    {
        private readonly ResourcePlatformSurface _target;

        public DebugView(ResourcePlatformSurface value)
        {
            _target = value;
        }

        public ResourceExtensionKey Key => _target.Key;

        public JsonElement Schema => _target.Schema;

        public JsonElement Value => _target.Value;
    }
}
