using System;

using Assimalign.Cohesion.Web.Validation.Internal;

namespace Assimalign.Cohesion.Web.Validation;

/// <summary>
/// Builder-time registration of request validation.
/// </summary>
/// <remarks>
/// Registration is dependency-free per the area's composition model: the validators and the default are
/// captured in an application feature the host seeds onto every exchange. No service container,
/// configuration binding, or request-time service location is involved, and no middleware is added:
/// source-generated typed endpoints validate the values they bind, and handlers call
/// <c>context.ValidateAsync(value)</c>.
/// </remarks>
public static class ValidationWebApplicationExtensions
{
    extension(IWebApplicationBuilder builder)
    {
        /// <summary>
        /// Registers request validation: the validator for each model type, and whether bound values are
        /// validated by default.
        /// </summary>
        /// <remarks>
        /// The options are read once, when this method returns; later changes to them are not observed.
        /// Registering again replaces the earlier registration.
        /// </remarks>
        /// <param name="configure">Registers the validators and sets the default.</param>
        /// <returns>The web application builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        public IWebApplicationBuilder AddValidation(Action<EndpointValidationOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            EndpointValidationOptions options = new();
            configure(options);

            return builder.AddFeature(new EndpointValidationFeature(options.Enabled, options.Validators));
        }
    }
}
