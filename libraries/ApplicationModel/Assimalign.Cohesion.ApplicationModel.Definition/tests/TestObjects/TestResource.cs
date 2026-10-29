using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

/// <summary>
/// One running instance of <c>Web/orders-api</c> with a log-level control.
/// </summary>
internal sealed class TestResource : IApplicationResourceInstance
{
    public LogLevelAction LogLevel { get; } = new();

    public ProviderName Provider => "Web";

    public ResourceName Name => "orders-api";

    public ResourceInstanceKey Key { get; } = ResourceInstanceKey.New();

    public IReadOnlyCollection<IResourceAction> Actions => [LogLevel];

    public IReadOnlyCollection<IResourceHealthContributor> HealthContributors => [];
}
