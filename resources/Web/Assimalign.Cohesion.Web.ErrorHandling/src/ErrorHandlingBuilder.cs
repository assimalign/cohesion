using System;

using Assimalign.Cohesion.Web.ErrorHandling.Internal;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// The composition surface for the <c>OnError</c> hook, returned by <c>AddErrorHandling</c>.
/// Registrations are consulted in the order they are added; the first to handle a fault ends the
/// chain, so register specific handlers before general ones.
/// </summary>
/// <remarks>
/// Registration is composition-time only: the builder feeds the feature instance that
/// <c>AddErrorHandling</c> attached to the application, and the feature reads the registrations
/// live, so <see cref="OnError(HttpErrorHandler)"/> calls after the root verb still take effect.
/// Registering handlers while the application is serving is not supported. To replace the
/// terminal <c>ProblemDetails</c> default entirely, register a handler that always returns
/// <see langword="true"/> — the default only runs when every registration passes.
/// </remarks>
public sealed class ErrorHandlingBuilder
{
    private readonly HttpErrorHandlingFeature _feature;

    internal ErrorHandlingBuilder(HttpErrorHandlingFeature feature)
    {
        _feature = feature;
    }

    /// <summary>
    /// Appends a handler to the <c>OnError</c> chain.
    /// </summary>
    /// <param name="handler">The handler to consult when a fault escapes the pipeline.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    public ErrorHandlingBuilder OnError(IHttpErrorHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _feature.AddHandler(handler);
        return this;
    }

    /// <summary>
    /// Appends a delegate handler to the <c>OnError</c> chain.
    /// </summary>
    /// <param name="handler">
    /// The delegate to consult when a fault escapes the pipeline; return <see langword="true"/>
    /// to own the fault, <see langword="false"/> to pass it on.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    public ErrorHandlingBuilder OnError(HttpErrorHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _feature.AddHandler(new DelegateErrorHandler(handler));
        return this;
    }
}
