using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// A stateful control, shaped like an area's: a read reports the level, a write sets it.
/// </summary>
internal sealed class LogLevelAction : IResourceAction
{
    private readonly ResourceActionState<LogLevelSetting> _state =
        ResourceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), _ => { });

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
        if (setting is null)
        {
            return ResourceActionResult.Rejected("A log level is required.");
        }

        await _state.SetAsync(setting, cancellationToken);
        return ResourceActionResult.Success();
    }
}
