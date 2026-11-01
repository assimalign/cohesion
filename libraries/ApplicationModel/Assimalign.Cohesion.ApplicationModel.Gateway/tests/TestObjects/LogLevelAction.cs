using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A stateful control, shaped like an area's: a read reports the level, a write sets it.
/// </summary>
internal sealed class LogLevelAction : IApplicationInstanceAction
{
    private readonly ApplicationInstanceActionState<LogLevelSetting> _state =
        ApplicationInstanceActionState<LogLevelSetting>.Create(new LogLevelSetting("Information"), _ => { });

    public LogLevelSetting Current => _state.Value;

    public ActionName Name => "log-level";

    public ApplicationActionKind Kind => ApplicationActionKind.ReadWrite;

    public IApplicationInstanceActionState? State => _state;

    public async ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Operation == ApplicationActionOperation.Read)
        {
            return ApplicationActionResult.Success(_state.Value, context.Serializer);
        }

        LogLevelSetting? setting = context.ReadInput<LogLevelSetting>();
        if (setting is null)
        {
            return ApplicationActionResult.Rejected("A log level is required.");
        }

        await _state.SetAsync(setting, cancellationToken);
        return ApplicationActionResult.Success();
    }
}
