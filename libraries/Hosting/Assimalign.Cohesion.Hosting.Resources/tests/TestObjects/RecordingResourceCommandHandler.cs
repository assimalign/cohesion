using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

internal sealed class RecordingResourceCommandHandler : IResourceCommandHandler
{
    public string Kind => "test.set";
    internal int Executions { get; private set; }
    internal int Deletions { get; private set; }
    internal bool Reject { get; set; }

    public ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Reject)
        {
            throw new ResourceCommandRejectedException($"Provider rejected key '{command.Key}'.");
        }
        Executions++;
        return ValueTask.FromResult(command.Payload);
    }

    public ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deletions++;
        return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }
}
