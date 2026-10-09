using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A session whose cores begin <see cref="TestTransaction"/>s and record the requests they
/// execute; it exposes the base's protected operation hold and refusal check.
/// </summary>
internal sealed class TestSession : DatabaseSession
{
    private readonly List<TestTransaction> _begun = [];
    private int _executions;
    private int _beginCores;

    public TestSession(DatabaseInstance database, TestLog? log = null)
        : base(database)
    {
        Log = log ?? new TestLog();
    }

    public TestLog Log { get; }

    /// <summary>Gets or sets a task the begin core awaits, so a test can hold a BEGIN in flight.</summary>
    public Task? BeginBarrier { get; set; }

    /// <summary>Gets a signal set when a begin core starts.</summary>
    public TaskCompletionSource BeginStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets or sets a failure the disposal core throws after it ran.</summary>
    public Exception? DisposeFailure { get; set; }

    /// <summary>Gets or sets the offline refusal every transaction the session begins answers with.</summary>
    public DatabaseException? OfflineRefusal { get; set; }

    public int BeginCores => Volatile.Read(ref _beginCores);

    public int Executions => Volatile.Read(ref _executions);

    public IsolationLevel? LastIsolationLevel { get; private set; }

    public QueryRequest? LastRequest { get; private set; }

    public string? LastStatement { get; private set; }

    public IReadOnlyDictionary<string, object?>? LastParameters { get; private set; }

    public TestTransaction[] Begun
    {
        get
        {
            lock (_begun)
            {
                return [.. _begun];
            }
        }
    }

    /// <summary>
    /// Gets or sets what the execute cores throw instead of returning a result; null to return
    /// <see cref="Result"/>. A synchronous core throws it inside <see cref="ExecuteGate"/>'s lock.
    /// </summary>
    public Exception? ExecuteFailure { get; set; }

    /// <summary>
    /// Gets or sets whether the execute cores return <see cref="ExecuteFailure"/> as a faulted task,
    /// as an <c>async</c> core that fails before its first await does, instead of throwing it.
    /// </summary>
    public bool ExecuteFailureAsynchronously { get; set; }

    /// <summary>
    /// Gets or sets a task the execute cores await before they complete, so a test can hold a
    /// statement pending.
    /// </summary>
    public Task? ExecuteBarrier { get; set; }

    /// <summary>
    /// Gets the lock a synchronous core holds while it throws <see cref="ExecuteFailure"/>, as a
    /// leaf that fails under its own lock does.
    /// </summary>
    public object ExecuteGate { get; } = new();

    /// <summary>
    /// Gets or sets the result the execute cores return.
    /// </summary>
    public QueryResult Result { get; set; } = TestResult.Instance;

    /// <summary>
    /// Calls the typed execute core directly, past the base's public member: the baseline the
    /// event source's allocation check compares the public member with.
    /// </summary>
    public ValueTask<QueryResult> ExecuteCoreDirectly(QueryRequest request, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(request, cancellationToken);

    public bool EnterOperation() => TryEnterOperation();

    public void LeaveOperation() => ExitOperation();

    public void ThrowIfRefused() => ThrowIfTransactionRefuses();

    public void ThrowIfSessionClosed() => ThrowIfClosed();

    protected override async ValueTask<DatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _beginCores);
        LastIsolationLevel = isolationLevel;
        BeginStarted.TrySetResult();
        if (BeginBarrier is { } barrier)
        {
            await barrier.ConfigureAwait(false);
        }

        var transaction = new TestTransaction(isolationLevel) { OfflineRefusal = OfflineRefusal };
        lock (_begun)
        {
            _begun.Add(transaction);
        }

        return transaction;
    }

    protected override ValueTask<QueryResult> ExecuteCoreAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executions);
        LastRequest = request;
        return Complete();
    }

    protected override ValueTask<QueryResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executions);
        LastStatement = statement;
        LastParameters = parameters;
        return Complete();
    }

    protected override ValueTask DisposeAsyncCore()
    {
        // Records the transaction's state as the leaf's teardown sees it: still open, before the
        // base ends it.
        Log.Add("session:operations:" + (CurrentTransaction?.State.ToString() ?? "none"));
        if (DisposeFailure is { } failure)
        {
            throw failure;
        }

        return ValueTask.CompletedTask;
    }

    // The execute cores' outcome: Result, or ExecuteFailure thrown under ExecuteGate's lock or
    // returned faulted; after ExecuteBarrier when one is set.
    private ValueTask<QueryResult> Complete()
    {
        if (ExecuteBarrier is { } barrier)
        {
            return CompleteAfterAsync(barrier, ExecuteFailure);
        }

        if (ExecuteFailure is { } failure)
        {
            if (ExecuteFailureAsynchronously)
            {
                return ValueTask.FromException<QueryResult>(failure);
            }

            lock (ExecuteGate)
            {
                throw failure;
            }
        }

        return new ValueTask<QueryResult>(Result);
    }

    private async ValueTask<QueryResult> CompleteAfterAsync(Task barrier, Exception? failure)
    {
        await barrier.ConfigureAwait(false);
        if (failure is not null)
        {
            throw failure;
        }

        return Result;
    }
}
