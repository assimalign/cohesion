using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// The delegate form of an <c>OnError</c> registration — see <see cref="IHttpErrorHandler"/> for
/// the chain semantics. Return <see langword="true"/> to own the fault (the response must be
/// written), <see langword="false"/> to pass it to the next registration.
/// </summary>
/// <param name="context">The exchange whose pipeline faulted.</param>
/// <param name="exception">The fault that escaped the pipeline.</param>
/// <param name="cancellationToken">A token that cancels response writing.</param>
/// <returns><see langword="true"/> when the fault was handled; otherwise <see langword="false"/>.</returns>
public delegate ValueTask<bool> HttpErrorHandler(IHttpContext context, Exception exception, CancellationToken cancellationToken);
