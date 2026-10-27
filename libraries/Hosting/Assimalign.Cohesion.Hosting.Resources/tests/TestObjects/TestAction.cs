using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>
/// A configurable action for edge cases; by default a stateless write that succeeds.
/// </summary>
internal sealed class TestAction : IResourceAction
{
    public ResourceActionName Name { get; init; } = "flush-cache";

    public ResourceActionKind Kind { get; init; } = ResourceActionKind.Write;

    public IResourceActionState? State { get; init; }

    public Func<ResourceActionContext, ValueTask<ResourceActionResult>> Handler { get; init; } =
        _ => ValueTask.FromResult(ResourceActionResult.Success());

    public int Invocations { get; private set; }

    public ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        Invocations++;
        return Handler.Invoke(context);
    }
}
