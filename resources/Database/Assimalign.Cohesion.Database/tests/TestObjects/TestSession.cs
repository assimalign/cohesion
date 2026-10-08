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
    /// <see cref="Result"/>.
    /// </summary>
    public Exception? ExecuteFailure { get; set; }

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
        if (ExecuteFailure is { } failure)
        {
            throw failure;
        }

        return new ValueTask<QueryResult>(Result);
    }

    protected override ValueTask<QueryResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executions);
        LastStatement = statement;
        LastParameters = parameters;
        if (ExecuteFailure is { } failure)
        {
            throw failure;
        }

        return new ValueTask<QueryResult>(Result);
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
}
