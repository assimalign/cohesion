using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One action of a resource's runtime surface: an imperative operation, such as flushing a cache, that the gateway
/// forwards to one instance or to every instance and stores nothing about.
/// </summary>
[DebuggerDisplay("{Name,nq} ({Target})")]
public sealed class ResourceSurfaceAction
{
    internal ResourceSurfaceAction(string name, ResourceSurfaceActionTarget target, JsonElement input, JsonElement result)
    {
        Name = name;
        Target = target;
        Input = input;
        Result = result;
    }

    /// <summary>Gets the action name: lowercase ASCII letters and digits in hyphen-separated words.</summary>
    public string Name { get; }

    /// <summary>Gets which instances run the action.</summary>
    public ResourceSurfaceActionTarget Target { get; }

    /// <summary>Gets the JSON schema of the action's input.</summary>
    public JsonElement Input { get; }

    /// <summary>Gets the JSON schema of the action's result.</summary>
    public JsonElement Result { get; }

    /// <summary>Validates an action input against <see cref="Input"/>.</summary>
    /// <param name="input">The complete input value.</param>
    /// <param name="error">Why the input does not conform, when it does not.</param>
    /// <returns>Whether the input conforms.</returns>
    public bool TryValidateInput(JsonElement input, [NotNullWhen(false)] out string? error)
    {
        error = input.ValueKind == JsonValueKind.Undefined
            ? ErrorMessages.ResourceActionInputInvalid
            : ResourceSurfaceSchemaValidator.ValidateValue(Input, input);
        return error is null;
    }
}
