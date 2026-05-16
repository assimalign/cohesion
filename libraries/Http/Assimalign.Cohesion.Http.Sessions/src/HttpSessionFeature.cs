using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpSessionFeature"/> implementation installed by
/// <see cref="HttpContextSessionExtensions.Session"/> when no feature is present.
/// </summary>
internal sealed class HttpSessionFeature : IHttpSessionFeature
{
    public HttpSessionFeature(IHttpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
    }

    public IHttpSession Session { get; set; }
}
