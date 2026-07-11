using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

/// <summary>
/// The <c>OnError</c> chain behind <see cref="IHttpErrorHandlingFeature"/>: a builder-time
/// singleton seeded onto every exchange. Registrations mutate a copy-on-write array under a lock
/// so the per-request read path is lock-free; mutation happens only during composition.
/// </summary>
internal sealed class HttpErrorHandlingFeature : IHttpErrorHandlingFeature
{
    private readonly object _gate = new();
    private IHttpErrorHandler[] _handlers = [];

    /// <inheritdoc />
    public string Name => nameof(HttpErrorHandlingFeature);

    /// <inheritdoc />
    public IReadOnlyList<IHttpErrorHandler> Handlers => _handlers;

    internal void AddHandler(IHttpErrorHandler handler)
    {
        lock (_gate)
        {
            _handlers = [.. _handlers, handler];
        }
    }

    /// <inheritdoc />
    public async ValueTask HandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        IHttpErrorHandler[] handlers = _handlers;

        foreach (IHttpErrorHandler handler in handlers)
        {
            if (await handler.TryHandleAsync(context, exception, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        await ProblemDetailsErrorHandler.Instance.TryHandleAsync(context, exception, cancellationToken).ConfigureAwait(false);
    }
}
