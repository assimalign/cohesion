using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>Observes cancellation only after the test synchronously releases its core.</summary>
internal sealed class InlineCancellationExchange : DatabaseProtocolExchange<bool>
{
    private readonly Action _onCancellation;
    // Deliberately inline: AllowExit must drain the exchange and lease's continuation chain
    // until cleanup completes or suspends. A queued continuation would lose that barrier.
    private readonly TaskCompletionSource _continue = new();

    internal InlineCancellationExchange(Action onCancellation)
        : base(StreamingClientTestHarness.Family)
    {
        _onCancellation = onCancellation;
    }

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken OperationToken { get; private set; }

    internal void AllowExit() => _continue.TrySetResult();

    protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader,
        ProtocolFrameWriter writer, CancellationToken cancellationToken)
    {
        OperationToken = cancellationToken;
        // The operation source owns this registration. The regression is precisely its
        // premature removal when source disposal races an already-queued dispatcher.
        cancellationToken.Register(_onCancellation);
        Started.TrySetResult();
        await _continue.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }
}
