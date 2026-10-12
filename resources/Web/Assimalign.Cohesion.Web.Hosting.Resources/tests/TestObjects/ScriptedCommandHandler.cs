using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

internal sealed class ScriptedCommandHandler : IResourceCommandHandler
{
    internal ScriptedCommandHandler(string kind)
    {
        Kind = kind;
    }

    public string Kind { get; }

    internal Exception? Failure { get; set; }

    public ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }

    public ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, cancellationToken);
}
