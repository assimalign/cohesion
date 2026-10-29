using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// A stateful control, shaped like an area's: a read reports the level, a write sets it.
/// </summary>
internal sealed class LogLevelAction : IApplicationInstanceAction
{
    private readonly ApplicationActionState<LogLevelSetting> _state;

    public LogLevelAction(string initialLevel = "Information")
    {
        _state = ApplicationActionState<LogLevelSetting>.Create(new LogLevelSetting(initialLevel), setting => Applied.Add(setting));
    }

    public List<LogLevelSetting> Applied { get; } = [];

    public LogLevelSetting Current => _state.Value;

    public ActionName Name => "log-level";

    public ApplicationActionKind Kind => ApplicationActionKind.ReadWrite;

    public IApplicationActionState? State => _state;

    public async ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Operation == ApplicationActionKind.Read)
        {
            return ApplicationActionResult.Success(_state.Value, context.Serializer);
        }

        LogLevelSetting? setting = context.ReadInput<LogLevelSetting>();
        if (setting is null || string.IsNullOrWhiteSpace(setting.Level))
        {
            return ApplicationActionResult.Rejected("A log level is required.");
        }

        await _state.SetAsync(setting, cancellationToken);
        return ApplicationActionResult.Success();
    }
}
