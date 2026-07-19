using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ErrorHandling.Internal;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// Builder-time registration of the <c>OnError</c> hook. Registration is dependency-free per the
/// area's composition model: the hook is attached to the application as a typed feature
/// (<see cref="IErrorHandlingFeature"/>) holding plain handler values — no service container,
/// no configuration binding, no request-time service location.
/// </summary>
public static class ErrorHandlingWebApplicationExtensions
{
    extension(IWebApplicationBuilder builder)
    {
        /// <summary>
        /// Adds the <c>OnError</c> hook to the application and returns the builder used to
        /// register fault handlers: <c>builder.AddErrorHandling().OnError(...)</c>. With no
        /// registrations, the hook is the terminal default alone — every fault renders as the
        /// RFC 9457 <c>ProblemDetails</c> payload.
        /// </summary>
        /// <returns>An <see cref="ErrorHandlingBuilder"/> for registering handlers.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// Call once per application: each call composes a fresh hook whose feature replaces the
        /// previously-registered one (features are name-keyed). Register multiple handlers by
        /// chaining <see cref="ErrorHandlingBuilder.OnError(HttpErrorHandler)"/> on the returned
        /// builder — the registration order is the consultation order.
        /// </remarks>
        public ErrorHandlingBuilder AddErrorHandling()
        {
            ArgumentNullException.ThrowIfNull(builder);

            ErrorHandlingFeature feature = new();
            builder.AddFeature(feature);

            return new ErrorHandlingBuilder(feature);
        }
    }
}
