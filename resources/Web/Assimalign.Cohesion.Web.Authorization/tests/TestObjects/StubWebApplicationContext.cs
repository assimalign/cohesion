using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

/// <summary>
/// A minimal <see cref="IWebApplicationContext"/> over a fixed feature list (typically the features a
/// service provider resolved): the only context capability
/// <c>TryGetAuthorizationOptions</c> reads.
/// </summary>
internal sealed class StubWebApplicationContext : IWebApplicationContext
{
    public StubWebApplicationContext(IEnumerable<IHttpFeature> features)
    {
        Features = features;
    }

    public FileSystemPath? ContentRootPath => null;

    public FileSystemPath? WebRootPath => null;

    public IEnumerable<IWebApplicationMiddleware> Middleware => [];

    public IEnumerable<IWebApplicationServer> Servers => [];

    public IEnumerable<IHttpFeature> Features { get; }
}
