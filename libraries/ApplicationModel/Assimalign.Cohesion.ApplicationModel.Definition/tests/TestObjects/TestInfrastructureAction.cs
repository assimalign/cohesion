using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

/// <summary>
/// A write-only infrastructure action that counts its invocations.
/// </summary>
internal sealed class TestInfrastructureAction : IApplicationResourceAction
{
    public TestInfrastructureAction(string name)
    {
        Name = name;
    }

    public int Invocations { get; private set; }

    public ResourceActionName Name { get; }

    public ResourceActionKind Kind => ResourceActionKind.Write;

    public ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        Invocations++;
        return ValueTask.FromResult(ResourceActionResult.Success());
    }
}
