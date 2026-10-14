using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

internal static class ErrorHandlerInvoker
{
    // Capture even a synchronous handler throw in an awaitable before the reporting filter sees it.
    // Otherwise the filter runs before the handler's finally blocks, which may cancel the exchange.
    public static async ValueTask<bool> InvokeAsync(
        IErrorHandler handler, IHttpContext context, Exception exception, CancellationToken cancellationToken)
        => await handler.TryHandleAsync(context, exception, cancellationToken).ConfigureAwait(false);
}
