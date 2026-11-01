using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A write-only infrastructure action that counts its invocations.
/// </summary>
internal sealed class TestInfrastructureAction : IApplicationGatewayDeploymentAction
{
    public TestInfrastructureAction(string name)
    {
        Name = name;
    }

    public int Invocations { get; private set; }

    public ActionName Name { get; }

    public ApplicationActionKind Kind => ApplicationActionKind.Write;

    public ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        Invocations++;
        return ValueTask.FromResult(ApplicationActionResult.Success());
    }
}
