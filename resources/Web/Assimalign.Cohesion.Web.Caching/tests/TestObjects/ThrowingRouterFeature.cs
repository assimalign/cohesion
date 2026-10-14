using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

/// <summary>
/// A per-application routing feature whose router cannot be reached: any access throws. Installed on an
/// exchange, it proves the output-cache middleware reads the endpoint routing published instead of
/// running the matcher a second time.
/// </summary>
internal sealed class ThrowingRouterFeature : IRouterFeature
{
    public string Name => nameof(IRouterFeature);

    public IRouter Router => throw new InvalidOperationException("The output cache must not run the route matcher.");

    public IRouterBuilder Builder => throw new InvalidOperationException("The output cache must not map routes.");
}
