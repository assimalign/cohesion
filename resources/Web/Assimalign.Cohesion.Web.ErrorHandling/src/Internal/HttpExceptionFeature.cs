using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

/// <summary>
/// The default <see cref="IHttpExceptionFeature"/> the exception boundary publishes: carries the
/// caught exception and the request path captured when the boundary intercepted the fault.
/// </summary>
internal sealed class HttpExceptionFeature : IHttpExceptionFeature
{
    public HttpExceptionFeature(Exception error, HttpPath path)
    {
        Error = error;
        Path = path;
    }

    /// <inheritdoc />
    public string Name => nameof(IHttpExceptionFeature);

    /// <inheritdoc />
    public Exception Error { get; }

    /// <inheritdoc />
    public HttpPath Path { get; }
}
