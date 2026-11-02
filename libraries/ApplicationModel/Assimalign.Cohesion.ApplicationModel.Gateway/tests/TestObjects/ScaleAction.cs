using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An infrastructure action that sets how many instances the platform runs.
/// </summary>
internal sealed class ScaleAction : IApplicationGatewayDeploymentAction
{
    public int Replicas { get; private set; } = 1;

    public ActionName Name => "scale";

    public ApplicationActionKind Kind => ApplicationActionKind.Write;

    public ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        ScaleSetting? setting = context.ReadInput<ScaleSetting>();
        if (setting is null || setting.Replicas < 0)
        {
            return ValueTask.FromResult(ApplicationActionResult.Rejected("Replicas must be zero or more."));
        }

        Replicas = setting.Replicas;
        return ValueTask.FromResult(ApplicationActionResult.Success());
    }
}
