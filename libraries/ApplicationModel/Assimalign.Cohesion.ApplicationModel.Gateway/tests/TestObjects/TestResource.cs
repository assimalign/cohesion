using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// One running instance of <c>Web/orders-api</c> with a log-level control.
/// </summary>
internal sealed class TestResource : IApplicationResourceInstance
{
    public LogLevelAction LogLevel { get; } = new();

    public ProviderName Provider => "Web";

    public ResourceName Name => "orders-api";

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment { get; init; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions => [LogLevel];

    public IReadOnlyCollection<IApplicationHealthContributor> HealthContributors => [];
}
