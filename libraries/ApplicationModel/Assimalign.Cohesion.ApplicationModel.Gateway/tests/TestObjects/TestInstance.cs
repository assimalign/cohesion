using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// One running instance of a resource, with a stateful log-level control.
/// </summary>
internal sealed class TestInstance : IResource, IResourceAction
{
    private readonly ResourceActionState<LogLevelSetting> _state =
        ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), _ => { });

    public TestInstance(ProviderName provider, ResourceName name)
    {
        Provider = provider;
        Name = name;
    }

    public LogLevelSetting Level => _state.Value;

    public ProviderName Provider { get; }

    public ResourceName Name { get; }

    public ResourceInstanceKey Key { get; } = ResourceInstanceKey.New();

    public IReadOnlyCollection<IResourceAction> Actions => [this];

    public IReadOnlyCollection<IResourceHealthContributor> HealthContributors => [];

    ResourceActionName IResourceAction.Name => "log-level";

    ResourceActionKind IResourceAction.Kind => ResourceActionKind.ReadWrite;

    IResourceActionState? IResourceAction.State => _state;

    async ValueTask<ResourceActionResult> IResourceAction.ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken)
    {
        if (context.Operation == ResourceActionKind.Read)
        {
            return ResourceActionResult.Success(_state.Value, context.Serializer);
        }

        await _state.SetAsync(context.ReadInput<LogLevelSetting>()!, cancellationToken);
        return ResourceActionResult.Success();
    }
}
