using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A transaction over an in-memory kernel transaction: implements only the base's protected cores
/// and vocabulary, and exposes the protected members a model's session and operations use.
/// </summary>
internal sealed class TestTransaction : DatabaseTransaction
{
    /// <summary>The code the double's aborted errors lead with.</summary>
    public const string AbortedCode = "TESTX001";

    private readonly object _kernelSync = new();
    private TransactionState _kernelState = TransactionState.Active;
    private int _commits;
    private int _rollbacks;
    private int _disposeCores;

    public TestTransaction(IsolationLevel isolationLevel = IsolationLevel.Snapshot)
        : base(TransactionId.NewId(), isolationLevel)
    {
    }

    /// <summary>Gets or sets the kernel transaction's state, as the kernel would change it under the caller.</summary>
    public TransactionState KernelState
    {
        get
        {
            lock (_kernelSync)
            {
                return _kernelState;
            }
        }
        set
        {
            lock (_kernelSync)
            {
                _kernelState = value;
            }
        }
    }

    /// <summary>Gets or sets the refusal an offline database answers with, or null while it is online.</summary>
    public DatabaseException? OfflineRefusal { get; set; }

    /// <summary>Gets or sets a task the commit core awaits once it started, so a test can hold it there.</summary>
    public Task? CommitBarrier { get; set; }

    /// <summary>Gets or sets a task the rollback core awaits once it started, so a test can hold it there.</summary>
    public Task? RollbackBarrier { get; set; }

    /// <summary>Gets or sets a failure the commit core throws after the kernel aborted the commit.</summary>
    public Exception? CommitFailure { get; set; }

    /// <summary>Gets or sets a failure the rollback core throws before the kernel started the rollback (the database is closing).</summary>
    public Exception? RollbackRefusal { get; set; }

    /// <summary>Gets a signal set when a commit core starts.</summary>
    public TaskCompletionSource CommitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets a signal set when a rollback core starts.</summary>
    public TaskCompletionSource RollbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets how many times the commit core ran.</summary>
    public int Commits => Volatile.Read(ref _commits);

    /// <summary>Gets how many times the rollback core ran.</summary>
    public int Rollbacks => Volatile.Read(ref _rollbacks);

    /// <summary>Gets how many times the disposal core ran.</summary>
    public int DisposeCores => Volatile.Read(ref _disposeCores);

    public bool Open => IsOpen;

    public bool Usable => IsUsable;

    public int Operations => RunningOperations;

    public ValueTask Abort(Exception cause) => AbortAsync(cause);

    public ValueTask Close(Exception cause) => CloseAsync(cause);

    public bool BeginOperation() => TryBeginOperation();

    public void FinishOperation() => EndOperation();

    public DatabaseException Refusal() => CreateRefusal();

    protected override TransactionState GetKernelState() => KernelState;

    protected override async ValueTask CommitCoreAsync()
    {
        Interlocked.Increment(ref _commits);
        CommitStarted.TrySetResult();
        if (CommitBarrier is { } barrier)
        {
            await barrier.ConfigureAwait(false);
        }

        if (CommitFailure is { } failure)
        {
            // The kernel aborts a commit it cannot complete.
            KernelState = TransactionState.RolledBack;
            throw failure;
        }

        KernelState = TransactionState.Committed;
    }

    protected override async ValueTask RollbackCoreAsync()
    {
        Interlocked.Increment(ref _rollbacks);
        RollbackStarted.TrySetResult();
        if (RollbackBarrier is { } barrier)
        {
            await barrier.ConfigureAwait(false);
        }

        if (RollbackRefusal is { } refusal)
        {
            // Refused before it started: the kernel transaction stays active.
            throw refusal;
        }

        KernelState = TransactionState.RolledBack;
    }

    protected override DatabaseException? GetOfflineRefusal() => OfflineRefusal;

    protected override DatabaseException CreateAbortedException(Exception? cause, bool commit)
    {
        string reason = cause is null ? string.Empty : " Cause: " + cause.Message;
        string message = commit
            ? AbortedCode + ": The session's transaction is aborted and cannot commit; nothing was committed." + reason
            : AbortedCode + ": The session's transaction is aborted; operations are refused until it is rolled back." + reason;
        return new DatabaseException(message, cause);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        Interlocked.Increment(ref _disposeCores);
        return ValueTask.CompletedTask;
    }
}
