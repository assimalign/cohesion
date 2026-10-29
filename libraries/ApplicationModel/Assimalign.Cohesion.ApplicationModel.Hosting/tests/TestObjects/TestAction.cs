using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// A configurable action for edge cases; by default a stateless write that succeeds.
/// </summary>
internal sealed class TestAction : IApplicationInstanceAction
{
    public ActionName Name { get; init; } = "flush-cache";

    public ApplicationActionKind Kind { get; init; } = ApplicationActionKind.Write;

    public IApplicationActionState? State { get; init; }

    public Func<ApplicationActionContext, ValueTask<ApplicationActionResult>> Handler { get; init; } =
        _ => ValueTask.FromResult(ApplicationActionResult.Success());

    public int Invocations { get; private set; }

    public ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        Invocations++;
        return Handler.Invoke(context);
    }
}
