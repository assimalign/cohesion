using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ResourceControlPlaneEventSourceCollection
{
    public const string Name = "Resource control-plane EventSource";
}
