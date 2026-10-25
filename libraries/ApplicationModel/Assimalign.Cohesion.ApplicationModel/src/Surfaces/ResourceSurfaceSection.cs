using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One configuration section of a resource's runtime surface: a value a deployment may set and, when the section
/// allows it, the management API may change until the resource's next deployment.
/// </summary>
/// <remarks>
/// The resource holds the section's baseline (its own code and appsettings); a gateway only knows the section's
/// shape, so it validates merge patches against <see cref="Schema"/> and never computes the effective value.
/// </remarks>
[DebuggerDisplay("{Path} ({Mutability}, {Scope})")]
public sealed class ResourceSurfaceSection
{
    internal ResourceSurfaceSection(
        ResourceSectionPath path,
        ResourceSurfaceMutability mutability,
        ResourceSurfaceScope scope,
        bool claimable,
        JsonElement schema)
    {
        Path = path;
        Mutability = mutability;
        Scope = scope;
        Claimable = claimable;
        Schema = schema;
    }

    /// <summary>Gets the configuration path the deployment record uses for this section.</summary>
    public ResourceSectionPath Path { get; }

    /// <summary>Gets when the section may change after deployment.</summary>
    public ResourceSurfaceMutability Mutability { get; }

    /// <summary>Gets what applying the section changes.</summary>
    public ResourceSurfaceScope Scope { get; }

    /// <summary>Gets whether other applications may contribute keyed entries to this section through claims.</summary>
    public bool Claimable { get; }

    /// <summary>Gets the JSON schema of the section's value, with the titles and descriptions a portal renders.</summary>
    public JsonElement Schema { get; }

    /// <summary>
    /// Gets whether the management API may store an override for the section: it is not deployment-only, and it
    /// configures each instance's own process rather than shared backend data.
    /// </summary>
    public bool IsOverridable =>
        Mutability != ResourceSurfaceMutability.Deployment && Scope == ResourceSurfaceScope.Process;

    /// <summary>Validates an RFC 7396 merge patch for this section against <see cref="Schema"/>.</summary>
    /// <param name="patch">
    /// The merge patch. A null patch resets the section; an object patch merges member by member; any other value
    /// replaces the section's value.
    /// </param>
    /// <param name="error">Why the patch does not conform, when it does not.</param>
    /// <returns>Whether the patch conforms.</returns>
    /// <remarks>
    /// An unknown member, a value of the wrong type, a value outside an enumeration, or the removal of a required
    /// member fails. An object schema that lists its members is closed, because the resource's options type would
    /// silently drop any member it does not declare.
    /// </remarks>
    public bool TryValidatePatch(JsonElement patch, [NotNullWhen(false)] out string? error)
    {
        error = patch.ValueKind == JsonValueKind.Undefined
            ? ErrorMessages.ResourceConfigurationPatchInvalid
            : ResourceSurfaceSchemaValidator.ValidatePatch(Schema, patch);
        return error is null;
    }
}
