using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

/// <summary>
/// A minimal <see cref="IWebApplicationBuilder"/> that records registered features: the only builder
/// capability <c>AddAuthorization</c> composes against.
/// </summary>
internal sealed class StubWebApplicationBuilder : IWebApplicationBuilder
{
    public List<IHttpFeature> Features { get; } = new();

    public IWebApplicationBuilder AddFeature(IHttpFeature feature)
    {
        Features.Add(feature);
        return this;
    }

    public IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure) => this;

    public IWebApplicationBuilder AddServer(IWebApplicationServer server) => this;

    public IWebApplicationBuilder AddServer(Func<IWebApplicationContext, IWebApplicationServer> server) => this;

    public IWebApplicationBuilder AddPipeline(IWebApplicationPipeline pipeline) => this;

    public IWebApplication Build() => throw new NotSupportedException();
}
