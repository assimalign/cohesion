using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// One running instance of <c>Web/orders-api</c> with a log-level control.
/// </summary>
internal sealed class TestResource : IResource
{
    public LogLevelAction LogLevel { get; } = new();

    public ProviderName Provider => "Web";

    public ResourceName Name => "orders-api";

    public ResourceInstanceKey Key { get; } = ResourceInstanceKey.New();

    public IReadOnlyCollection<IResourceAction> Actions => [LogLevel];

    public IReadOnlyCollection<IResourceHealthContributor> HealthContributors => [];
}
