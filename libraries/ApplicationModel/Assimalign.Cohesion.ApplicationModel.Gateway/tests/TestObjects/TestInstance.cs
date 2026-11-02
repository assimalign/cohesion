using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// One running instance of a resource, with a stateful log-level control.
/// </summary>
internal sealed class TestInstance : IApplicationInstance, IApplicationInstanceAction
{
    private readonly ApplicationInstanceActionState<LogLevelSetting> _state =
        ApplicationInstanceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), _ => { });

    public TestInstance(ProviderName provider, ResourceName name, IReadOnlyDictionary<SettingName, string> settings, DeploymentId? deployment)
    {
        Provider = provider;
        Name = name;
        Settings = settings;
        Deployment = deployment;
    }

    public LogLevelSetting Level => _state.Value;

    /// <summary>The settings the platform started the instance with, as its configuration would read them.</summary>
    public IReadOnlyDictionary<SettingName, string> Settings { get; }

    public ProviderName Provider { get; }

    public ResourceName Name { get; }

    public InstanceKey Key { get; } = InstanceKey.New();

    /// <summary>The deployment that started the instance, which its workload ran with.</summary>
    public DeploymentId? Deployment { get; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions => [this];

    public IReadOnlyCollection<IApplicationInstanceHealthContributor> HealthContributors => [];

    ActionName IApplicationInstanceAction.Name => "log-level";

    ApplicationActionKind IApplicationInstanceAction.Kind => ApplicationActionKind.ReadWrite;

    IApplicationInstanceActionState? IApplicationInstanceAction.State => _state;

    async ValueTask<ApplicationActionResult> IApplicationInstanceAction.ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken)
    {
        if (context.Operation == ApplicationActionOperation.Read)
        {
            return ApplicationActionResult.Success(_state.Value, context.Serializer);
        }

        await _state.SetAsync(context.ReadInput<LogLevelSetting>()!, cancellationToken);
        return ApplicationActionResult.Success();
    }
}
