using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Sessions.Internal;

/// <summary>
/// The session middleware: installs a lazy, store-backed session feature on the
/// exchange before the pipeline runs and commits it after the pipeline unwinds.
/// Nothing is loaded, persisted, or cookied unless the application actually
/// touches the session (see <see cref="WebSessionFeature"/>).
/// </summary>
/// <remarks>
/// The store and options are captured once at builder time; the middleware
/// resolves nothing per request. It is dependency-free — no service container,
/// no configuration binding — in keeping with the feature-package composition
/// rules.
/// </remarks>
internal sealed class SessionMiddleware : IWebApplicationMiddleware
{
    private readonly IHttpSessionStore _store;
    private readonly HttpSessionOptions _options;

    public SessionMiddleware(IHttpSessionStore store, HttpSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _options = options;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        WebSessionFeature feature = new(context, _store, _options);
        context.Features.Set<IHttpSessionFeature>(feature);

        await next.Invoke(context).ConfigureAwait(false);

        // Persist (or slide) only a session the application actually touched. The
        // commit path writes to the store only — the Set-Cookie was already queued
        // at establishment — so it is safe even after the response head committed.
        if (feature.WasAccessed)
        {
            await feature.CommitAsync(context.RequestCancelled).ConfigureAwait(false);
        }
    }
}
