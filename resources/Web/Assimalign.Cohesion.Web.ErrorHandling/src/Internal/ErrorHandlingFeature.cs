using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

/// <summary>
/// The <c>OnError</c> chain behind <see cref="IErrorHandlingFeature"/>: an application singleton
/// seeded onto every exchange. <see cref="ErrorHandlingBuilder.Build"/> hands it a snapshot of the
/// registrations, so the chain is immutable and the per-request read path takes no lock.
/// </summary>
internal sealed class ErrorHandlingFeature : IErrorHandlingFeature
{
    private readonly IErrorHandler[] _handlers;

    internal ErrorHandlingFeature(IErrorHandler[] handlers)
    {
        _handlers = handlers;
    }

    /// <inheritdoc />
    public string Name => nameof(ErrorHandlingFeature);

    /// <inheritdoc />
    public IReadOnlyList<IErrorHandler> Handlers => _handlers;

    /// <inheritdoc />
    public async ValueTask HandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        foreach (IErrorHandler handler in _handlers)
        {
            if (await handler.TryHandleAsync(context, exception, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        await ProblemDetailsErrorHandler.Instance.TryHandleAsync(context, exception, cancellationToken).ConfigureAwait(false);
    }
}
