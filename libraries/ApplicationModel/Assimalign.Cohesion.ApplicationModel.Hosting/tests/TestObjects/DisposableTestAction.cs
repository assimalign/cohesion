using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// An action that owns something it must release, which nothing disposes when a factory creates it.
/// </summary>
internal sealed class DisposableTestAction : IApplicationInstanceAction, IAsyncDisposable
{
    public ActionName Name { get; init; } = "drain-queue";

    public ApplicationActionKind Kind => ApplicationActionKind.Write;

    public IApplicationInstanceActionState? State => null;

    public ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(ApplicationActionResult.Success());
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
