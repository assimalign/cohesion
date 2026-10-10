using System;
using System.ComponentModel;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Validation.Internal;

namespace Assimalign.Cohesion.Web.Validation;

/// <summary>
/// The factory behind <c>builder.Services.AddValidation(...)</c>. Applications call that verb, not this
/// type.
/// </summary>
/// <remarks>
/// <para>
/// The verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>): every compilation
/// that references both this package and <c>Assimalign.Cohesion.DependencyInjection</c>, as every Web
/// application does through <c>Web.Hosting</c>, receives <c>AddValidation(configure)</c> on
/// <c>IServiceProviderBuilder</c>. The verb registers the feature <see cref="CreateFeature"/> returns as
/// an <see cref="IHttpFeature"/> singleton (owner decision 34, #1380). No middleware is added:
/// source-generated typed endpoints validate the values they bind, and handlers call
/// <c>context.ValidateAsync(value)</c>.
/// </para>
/// <para>
/// The type is public only because the generated verb, compiled into the application, calls it. It is
/// the static-factory shape so the verb keeps its <see cref="EndpointValidationOptions"/> callback:
/// the builder template needs a builder type with a <c>Build</c> method, which an options type is not.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ValidationComponents
{
    /// <summary>
    /// Creates the application's validation feature: the validator for each model type, and whether bound
    /// values are validated by default.
    /// </summary>
    /// <remarks>
    /// The options are read once, when <paramref name="configure"/> returns; later changes to them are not
    /// observed. Registering again replaces the earlier registration.
    /// </remarks>
    /// <param name="configure">Registers the validators and sets the default.</param>
    /// <returns>The validation feature, registered by the verb as an <see cref="IHttpFeature"/> singleton.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    public static IHttpFeature CreateFeature(Action<EndpointValidationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        EndpointValidationOptions options = new();
        configure(options);

        return new EndpointValidationFeature(options.Enabled, options.Validators);
    }
}
