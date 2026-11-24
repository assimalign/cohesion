using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// Event sources and their counters are process-wide, so the tests that listen to the proxy's source
/// or measure its counters run alone.
/// </summary>
[CollectionDefinition("Web.Proxy diagnostics", DisableParallelization = true)]
public sealed class WebProxyEventSourceCollection
{
}
