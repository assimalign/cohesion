using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

internal sealed class TransactionCommitLog : TransactionLog
{
    internal Action? DuringCommit { get; init; }
    internal Exception? Failure { get; init; }
    internal TaskCompletionSource? Entered { get; init; }
    internal TaskCompletionSource? Release { get; init; }
    internal int CommitCalls { get; private set; }
    internal int AbortCalls { get; private set; }
    internal CancellationToken CommitToken { get; private set; }

    public override ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default) => default;

    public override async ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        CommitCalls++;
        CommitToken = cancellationToken;
        Entered?.TrySetResult();
        if (Release is { } release) { await release.Task.WaitAsync(cancellationToken); }
        DuringCommit?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is { } failure) { throw failure; }
    }

    public override ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        AbortCalls++;
        return default;
    }
}
