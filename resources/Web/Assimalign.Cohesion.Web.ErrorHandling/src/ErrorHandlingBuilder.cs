using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.ErrorHandling.Internal;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// Composes the <c>OnError</c> hook: the fault handlers an application registers, consulted in the order
/// they are added. The first to handle a fault ends the chain, so register specific handlers before general
/// ones. It also configures the exception boundary that <c>UseErrorHandling()</c> places
/// (<see cref="Options"/>).
/// </summary>
/// <remarks>
/// <para>
/// Applications receive one in the <c>builder.Services.AddErrorHandling(errors => errors.OnError(...))</c>
/// callback. That verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>, owner
/// decision 34): it creates the builder, runs the callback, calls <see cref="Build"/>, and registers the
/// resulting <see cref="IErrorHandlingFeature"/> as an <c>IHttpFeature</c> singleton. A composition surface
/// without a service container registers <see cref="Build"/>'s result through
/// <c>IWebApplicationBuilder.AddFeature</c>.
/// </para>
/// <para>
/// To replace the terminal <c>ProblemDetails</c> default entirely, register a handler that always returns
/// <see langword="true"/>: the default only runs when every registration passes.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("ErrorHandlingBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class ErrorHandlingBuilder
{
    private readonly List<IErrorHandler> _handlers = new();

    /// <summary>
    /// Initializes a builder with no handlers and the boundary's production defaults. Built as is, it yields
    /// a hook that renders every fault as the RFC 9457 <c>ProblemDetails</c> payload, with no developer detail
    /// and no fault observer.
    /// </summary>
    public ErrorHandlingBuilder()
    {
    }

    /// <summary>
    /// Gets the options of the exception boundary <c>UseErrorHandling()</c> places: the developer-detail
    /// toggle, the fault-observation hook, and the diagnostics-suppression predicate.
    /// </summary>
    /// <remarks>
    /// The boundary reads them from the hook the application registered last; without a registration it
    /// runs with the defaults. <see cref="Build"/> copies them, so changing them afterwards has no effect.
    /// </remarks>
    public ExceptionBoundaryOptions Options { get; } = new();

    /// <summary>
    /// Appends a handler to the <c>OnError</c> chain.
    /// </summary>
    /// <param name="handler">The handler to consult when a fault escapes the pipeline.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    public ErrorHandlingBuilder OnError(IErrorHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handlers.Add(handler);
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

        _handlers.Add(new DelegateErrorHandler(handler));
        return this;
    }

    /// <summary>
    /// Builds the <c>OnError</c> hook from the handlers registered so far.
    /// </summary>
    /// <remarks>
    /// The hook holds a snapshot of the registrations and of <see cref="Options"/>: handlers added and
    /// options changed after this call do not reach it.
    /// </remarks>
    /// <returns>The hook, an <see cref="IErrorHandlingFeature"/> for the application to register.</returns>
    public IErrorHandlingFeature Build()
    {
        ExceptionBoundaryOptions boundary = new()
        {
            IncludeDeveloperDetails = Options.IncludeDeveloperDetails,
            OnException = Options.OnException,
            SuppressDiagnosticsCallback = Options.SuppressDiagnosticsCallback,
        };

        return new ErrorHandlingFeature(_handlers.ToArray(), boundary);
    }

    private sealed class DebugView
    {
        private readonly ErrorHandlingBuilder _target;

        public DebugView(ErrorHandlingBuilder value)
        {
            _target = value;
        }

        public ExceptionBoundaryOptions Options => _target.Options;
    }
}
