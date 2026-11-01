using System;
using System.Linq;

namespace Assimalign.Cohesion.Web.Query;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Query.Internal;

/// <summary>
/// Pipeline-builder verbs for the RFC 10008 QUERY server-side rules: request-content validation
/// (<see cref="WebApplicationExtensions.UseQueryValidation"/>) and conditional-QUERY evaluation
/// (<see cref="WebApplicationExtensions.UseQueryConditionals"/>). Each places the middleware the
/// application registered with <c>builder.Services.AddQueryValidation</c> or
/// <c>builder.Services.AddQueryConditionals</c>; the configuration lives on those registrations
/// (<see cref="WebQueryValidationBuilder"/>, <see cref="WebQueryConditionalsBuilder"/>), never on the
/// pipeline verbs.
/// </summary>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the middleware that enforces the RFC 10008 &#167; 2.1 / &#167; 2.3 QUERY
        /// request-content rules: the middleware the application registered with
        /// <c>builder.Services.AddQueryValidation(validation =&gt; ...)</c>, or one with the defaults when it
        /// registered none. Query content must declare a parseable <c>Content-Type</c> (rejected with 400
        /// or 415 per <see cref="WebQueryValidationOptions.InvalidContentTypeStatusCode"/>), the declared
        /// type must fall within the resource's <see cref="WebQueryValidationOptions.AcceptedMediaTypes"/>
        /// when configured (else 415), and the request's <c>Accept</c> field must be satisfiable against
        /// <see cref="WebQueryValidationOptions.SupportedResponseMediaTypes"/> when configured (else 406).
        /// Non-QUERY requests pass through untouched.
        /// </summary>
        /// <remarks>
        /// The options are configured where the middleware is registered
        /// (<see cref="WebQueryValidationBuilder"/>); this verb only positions it. When the application
        /// registered validation more than once, the last registration wins. The defaults accept any
        /// syntactically valid <c>Content-Type</c>, negotiate no response representation, and reject query
        /// content whose <c>Content-Type</c> is missing or malformed with <c>400 Bad Request</c>.
        /// </remarks>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseQueryValidation()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<WebQueryValidationMiddleware>().LastOrDefault()
                    ?? new WebQueryValidationBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }

        /// <summary>
        /// Places the middleware that evaluates a conditional QUERY exactly as the equivalent conditional
        /// GET (RFC 10008 &#167; 2.6): the middleware the application registered with
        /// <c>builder.Services.AddQueryConditionals(conditionals =&gt; conditionals.UseResourceValidators(...))</c>.
        /// The resource's current validators are resolved through the registered provider <em>before</em>
        /// the query executes, the precondition fields are evaluated via the core
        /// <c>HttpConditionalRequest</c> primitives, and a resolved precondition answers <c>304</c> /
        /// <c>412</c> without running the rest of the pipeline. Non-QUERY requests — and queries for which
        /// the provider returns <see langword="null"/> — pass through untouched.
        /// </summary>
        /// <remarks>
        /// The provider of the resource's validators is configured where the middleware is registered
        /// (<see cref="WebQueryConditionalsBuilder"/>); this verb only positions it. It has no default,
        /// because only the application can resolve its resources' validators, so composing the pipeline
        /// fails with an <see cref="InvalidOperationException"/> when the application registered none. When
        /// it registered more than one, the last registration wins.
        /// </remarks>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseQueryConditionals()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<WebQueryConditionalMiddleware>().LastOrDefault()
                    ?? throw new InvalidOperationException(ErrorMessages.QueryConditionalsNotRegistered);

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
