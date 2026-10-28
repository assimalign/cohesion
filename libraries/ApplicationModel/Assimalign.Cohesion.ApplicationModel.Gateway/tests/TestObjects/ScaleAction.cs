using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An infrastructure action that sets how many instances the platform runs.
/// </summary>
internal sealed class ScaleAction : IApplicationResourceAction
{
    public int Replicas { get; private set; } = 1;

    public ResourceActionName Name => "scale";

    public ResourceActionKind Kind => ResourceActionKind.Write;

    public ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        ScaleSetting? setting = context.ReadInput<ScaleSetting>();
        if (setting is null || setting.Replicas < 0)
        {
            return ValueTask.FromResult(ResourceActionResult.Rejected("Replicas must be zero or more."));
        }

        Replicas = setting.Replicas;
        return ValueTask.FromResult(ResourceActionResult.Success());
    }
}
