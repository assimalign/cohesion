using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// A pass-through <see cref="IWebApplicationMiddleware"/> standing in for another feature's registration in
/// the shared middleware aggregate. It counts its invocations, so a test can show it was never placed.
/// </summary>
internal sealed class CountingMiddleware : IWebApplicationMiddleware
{
    private int _invocations;

    /// <summary>Gets how many times the middleware ran.</summary>
    public int Invocations => Volatile.Read(ref _invocations);

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        Interlocked.Increment(ref _invocations);
        return next.Invoke(context);
    }
}
