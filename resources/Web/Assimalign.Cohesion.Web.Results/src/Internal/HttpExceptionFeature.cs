using System;

namespace Assimalign.Cohesion.Web.Results.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

/// <summary>
/// Default <see cref="IHttpExceptionFeature"/> implementation. Carries the caught exception and the
/// request path captured when the boundary intercepted the fault.
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
