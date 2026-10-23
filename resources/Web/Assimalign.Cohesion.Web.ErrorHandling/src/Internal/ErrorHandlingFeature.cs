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
[global::System.Diagnostics.DebuggerDisplay("ErrorHandlingFeature")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

        for (int index = 0; index < _handlers.Length; index++)
        {
            IErrorHandler handler = _handlers[index];
            try
            {
                if (await ErrorHandlerInvoker.InvokeAsync(handler, context, exception, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            // Deviates from the specific-catch rule: the false filter reports without catching
            // or replacing the handler's exception.
            catch (Exception failure) when (ErrorHandlingEventSource.Log.ErrorHandlerFailed(context, index, handler, failure))
            {
                throw;
            }
        }

        await ProblemDetailsErrorHandler.Instance.TryHandleAsync(context, exception, cancellationToken).ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        private readonly ErrorHandlingFeature _target;

        public DebugView(ErrorHandlingFeature value)
        {
            _target = value;
        }

        public IReadOnlyList<IErrorHandler> Handlers => _target.Handlers;
    }
}
