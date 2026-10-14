namespace Assimalign.Cohesion.Web.Validation;

/// <summary>
/// Endpoint metadata that declares whether the values bound for a route are validated:
/// <see cref="Required"/> validates them even where validation is off by default, and
/// <see cref="Disabled"/> exempts the route from it.
/// </summary>
/// <remarks>
/// <para>
/// Validation reads this metadata from the endpoint <c>UseRouting</c> published, last-wins
/// (<c>IRouterRouteMetadataCollection.GetMetadata&lt;ValidationMetadata&gt;</c>), so the most specific
/// declaration decides: a route's declaration overrides its group's, and either overrides the
/// application's default (<see cref="EndpointValidationOptions.Enabled"/>). The convention verbs
/// <c>RequireValidation()</c> and <c>DisableValidation()</c> append these instances.
/// </para>
/// <para>
/// Unlike antiforgery metadata, this carrier places no requirement on the pipeline: the source-generated
/// endpoint thunk validates the value it binds, so there is no middleware to register or acknowledge. An
/// endpoint that binds no validated value (a raw middleware endpoint, or a handler that binds only
/// scalars) is unaffected by it.
/// </para>
/// <para>
/// This sealed carrier is the metadata contract; there is deliberately no interface, following the
/// endpoint-metadata family rule in the Web.Routing DESIGN. Both states are instances of this one type so
/// a route's declaration supersedes its group's under last-wins resolution.
/// </para>
/// </remarks>
public sealed class ValidationMetadata
{
    private ValidationMetadata(bool requiresValidation)
    {
        RequiresValidation = requiresValidation;
    }

    /// <summary>
    /// Gets the shared metadata that validates the values bound for the endpoint it is attached to, even
    /// when validation is off by default.
    /// </summary>
    public static ValidationMetadata Required { get; } = new(requiresValidation: true);

    /// <summary>
    /// Gets the shared metadata that exempts the endpoint it is attached to from validation, overriding
    /// the application's default and a declaration on an enclosing route group.
    /// </summary>
    public static ValidationMetadata Disabled { get; } = new(requiresValidation: false);

    /// <summary>
    /// Gets whether the values bound for the endpoint are validated.
    /// </summary>
    public bool RequiresValidation { get; }
}
