using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// A stateful control, shaped like an area's: a read reports the level, a write sets it.
/// </summary>
internal sealed class LogLevelAction : IResourceAction
{
    private readonly ResourceActionState<LogLevelSetting> _state;

    public LogLevelAction(string initialLevel = "Information")
    {
        _state = ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting(initialLevel), setting => Applied.Add(setting));
    }

    public List<LogLevelSetting> Applied { get; } = [];

    public LogLevelSetting Current => _state.Value;

    public ResourceActionName Name => "log-level";

    public ResourceActionKind Kind => ResourceActionKind.ReadWrite;

    public IResourceActionState? State => _state;

    public async ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Operation == ResourceActionKind.Read)
        {
            return ResourceActionResult.Success(_state.Value, context.Serializer);
        }

        LogLevelSetting? setting = context.ReadInput<LogLevelSetting>();
        if (setting is null || string.IsNullOrWhiteSpace(setting.Level))
        {
            return ResourceActionResult.Rejected("A log level is required.");
        }

        await _state.SetAsync(setting, cancellationToken);
        return ResourceActionResult.Success();
    }
}
