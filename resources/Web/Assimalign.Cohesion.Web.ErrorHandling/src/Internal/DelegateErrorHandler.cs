using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

/// <summary>
/// Adapts an <see cref="HttpErrorHandler"/> delegate registration to the
/// <see cref="IErrorHandler"/> chain contract.
/// </summary>
internal sealed class DelegateErrorHandler : IErrorHandler
{
    private readonly HttpErrorHandler _handler;

    public DelegateErrorHandler(HttpErrorHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc />
    public ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken = default)
    {
        return _handler.Invoke(context, exception, cancellationToken);
    }
}
