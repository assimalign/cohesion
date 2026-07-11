using System;

namespace Assimalign.Cohesion.Web.Query;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Query.Internal;

/// <summary>
/// Pipeline-builder verbs for the RFC 10008 QUERY server-side rules: request-content validation
/// (<see cref="WebApplicationExtensions.UseQueryValidation"/>) and conditional-QUERY evaluation
/// (<see cref="WebApplicationExtensions.UseQueryConditionals"/>). Both compose against the root
/// pipeline seam with dependency-free registration — options and delegates only, no service
/// container.
/// </summary>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that enforces the RFC 10008 &#167; 2.1 / &#167; 2.3 QUERY
        /// request-content rules: query content must declare a parseable <c>Content-Type</c>
        /// (rejected with 400 or 415 per <see cref="WebQueryValidationOptions.InvalidContentTypeStatusCode"/>),
        /// the declared type must fall within the resource's
        /// <see cref="WebQueryValidationOptions.AcceptedMediaTypes"/> when configured (else 415),
        /// and the request's <c>Accept</c> field must be satisfiable against
        /// <see cref="WebQueryValidationOptions.SupportedResponseMediaTypes"/> when configured
        /// (else 406). Non-QUERY requests pass through untouched.
        /// </summary>
        /// <param name="configure">An optional callback that configures the validation options; the middleware snapshots the options here — later mutation has no effect.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseQueryValidation(Action<WebQueryValidationOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            var options = new WebQueryValidationOptions();
            configure?.Invoke(options);

            return builder.Use(new WebQueryValidationMiddleware(options));
        }

        /// <summary>
        /// Adds middleware that evaluates a conditional QUERY exactly as the equivalent
        /// conditional GET (RFC 10008 &#167; 2.6): the resource's current validators are resolved
        /// through <paramref name="validatorsProvider"/> <em>before</em> the query executes, the
        /// precondition fields are evaluated via the core <c>HttpConditionalRequest</c> primitives,
        /// and a resolved precondition answers <c>304</c> / <c>412</c> without running the rest of
        /// the pipeline. Non-QUERY requests — and queries for which the provider returns
        /// <see langword="null"/> — pass through untouched.
        /// </summary>
        /// <param name="validatorsProvider">Resolves the target resource's current validators (the same selected representation the equivalent GET would use).</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="validatorsProvider"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseQueryConditionals(WebQueryResourceValidatorsProvider validatorsProvider)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(validatorsProvider);

            return builder.Use(new WebQueryConditionalMiddleware(validatorsProvider));
        }
    }
}
