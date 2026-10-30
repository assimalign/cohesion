using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// An explicit ACID transaction of a SQL session, over an MVCC transaction context from the
/// database's transaction manager (the engine owns both vocabularies; this is the translation
/// boundary). Commit and rollback flow through the manager, whose journal-bound log owns the commit
/// record and its durability await; rollback undoes the writer's stamps through the version
/// store's ledger and releases its locks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The end state machine is the root base's</b> (<see cref="DatabaseTransaction"/>, the #1188
/// contract): commit, rollback, disposal and the session's teardown pass one end gate, so they
/// never race into the kernel; a transaction that did not commit accepts any number of rollbacks;
/// a token is observed only before a commit or rollback starts; a commit while a statement of the
/// transaction still runs is refused and leaves it active; and a transaction the kernel ended under
/// its caller (its database was dropped or closed, say) reports <see cref="TransactionState.Faulted"/>
/// and refuses statements, BEGIN and COMMIT with <c>CS04005</c> until the caller rolls it back.
/// This type supplies the model's vocabulary only: the kernel state, the kernel commit and rollback
/// with the engine's exception translation, the offline refusal (<c>CS04004</c>, #1243) and the
/// <c>CS04005</c> aborted error.
/// </para>
/// <para>
/// <b>A statement is statement-atomic</b>, so a failed statement leaves the transaction active (a
/// constraint violation, an evaluation fault, a result encoding or size failure, a deadlock victim).
/// Storage corruption or I/O failure
/// is the exception (#1364): it aborts the transaction through the base, retains the coded cause,
/// and requires rollback before further statements. A commit or rollback that started always ends
/// the context (#1226): the
/// kernel completes a started rollback whatever fails, keeping the locks of a writer whose undo it
/// must defer, and aborts a commit it cannot complete. A rollback ends the transaction even under a
/// running statement, which then fails and applies nothing.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of the root base with
/// an internal constructor; the session that began it is the only way to create one.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlDatabaseTransaction")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.SqlDatabaseTransaction.DebugView))]
public sealed class SqlDatabaseTransaction : DatabaseTransaction
{
    /// <summary>
    /// The code that leads the message of work refused because the session's transaction is
    /// aborted: the kernel ended it under its caller, or the session's teardown ended it.
    /// </summary>
    internal const string AbortedCode = "CS04005";

    // Aborted refusals are created on a normal path, so their messages are read once.
    private static readonly string _commitAborted = ErrorMessages.TransactionCommitAborted;
    private static readonly string _operationsAborted = ErrorMessages.TransactionOperationsAborted;
    private static readonly string _failureCause = ErrorMessages.TransactionFailureCause;

    private TransactionContext? _configurationPin;
    private bool _dataStatementStarted;

    // The database whose offline state (#1243) refuses the transaction's commit and rollback and
    // makes its disposal touch nothing.
    private readonly SqlDatabase _database;
    private SqlCatalogSnapshot? _catalogSnapshot;
    private Exception? _storageFailure;

    internal SqlDatabaseTransaction(TransactionCoordinator coordinator, TransactionContext context, SqlDatabase database, SqlCatalogSnapshot? catalogSnapshot, DateTimeOffset? transactionStart = null,
        TransactionContext? configurationPin = null)
        : base(coordinator, context)
    {
        TransactionStart = transactionStart ?? DateTimeOffset.UtcNow;
        _configurationPin = configurationPin;
        _database = database;
        _catalogSnapshot = catalogSnapshot;
    }

    /// <summary>
    /// Gets the MVCC transaction context statements execute under: the executor stamps writes with
    /// its sequence and resolves reads through its snapshot (re-captured per statement under
    /// <see cref="IsolationLevel.ReadCommitted"/>, fixed at begin under
    /// <see cref="IsolationLevel.Snapshot"/>).
    /// </summary>
    internal TransactionContext Context => KernelContext;

    internal DateTimeOffset TransactionStart { get; }

    /// <summary>
    /// Gets the catalog capture a <see cref="IsolationLevel.Snapshot"/> transaction took at BEGIN,
    /// which every system-view statement of the transaction reads; null for
    /// <see cref="IsolationLevel.ReadCommitted"/>, whose statements capture at their start.
    /// </summary>
    internal SqlCatalogSnapshot? CatalogSnapshot => IsolationLevel == IsolationLevel.Snapshot ? _catalogSnapshot : null;

    internal bool HasSavepointScope => HasSavepoints;

    internal bool TrySetIsolationLevel(IsolationLevel isolationLevel)
    {
        if (_dataStatementStarted) { return false; }
        return TryConfigureIsolationLevel(isolationLevel, () =>
        {
            if (_dataStatementStarted) { return false; }
            UpdateSavepointContext(KernelCoordinator.ReplaceIsolationLevel(KernelContext, isolationLevel));
            return true;
        });
    }

    internal async ValueTask StartDataStatementAsync()
    {
        // Admission is already held: configuration sees RunningOperations, including the
        // interval before this flag is published. Even a failing data statement closes SET.
        _dataStatementStarted = true;
        if (IsolationLevel == IsolationLevel.ReadCommitted) { _catalogSnapshot = null; }
        await ReleaseConfigurationPinAsync().ConfigureAwait(false);
    }

    // Test-only rendezvous and observation of the disposed-manager handoff. Neither
    // hook runs under a root/manager/context lock or changes production ownership.
    internal static Func<ValueTask>? ConfigurationPinReleasingAsync { get; set; }
    internal static Action? ConfigurationPinDisposalHandoff { get; set; }

    private async ValueTask ReleaseConfigurationPinAsync()
    {
        var pin = Interlocked.Exchange(ref _configurationPin, null);
        if (pin is not null && pin.State == TransactionState.Active && !_database.IsOffline)
        {
            try
            {
                if (ConfigurationPinReleasingAsync is { } releasing) { await releasing().ConfigureAwait(false); }
                await KernelCoordinator.RollbackAsync(pin).ConfigureAwait(false);
            }
            // Exchange gives this path sole ownership. A disposed manager, or an end
            // already claimed on this private pin, means kernel disposal owns its abort.
            // Pin teardown must not replace an already-completed transaction's outcome.
            catch (ObjectDisposedException) { ConfigurationPinDisposalHandoff?.Invoke(); }
            catch (TransactionAbortedException) { ConfigurationPinDisposalHandoff?.Invoke(); }
        }
    }

    /// <summary>Gets the number of admitted statements still running (observability for tests and diagnostics).</summary>
    internal int RunningStatements => RunningOperations;

    /// <summary>
    /// Admits one statement into the transaction, so a commit cannot start while it runs. Every
    /// admitted statement is paired with <see cref="EndStatement"/>.
    /// </summary>
    /// <returns>True when the transaction accepts the statement; false when it refuses statements.</returns>
    internal bool TryBeginStatement() => TryBeginOperation();

    /// <summary>Ends one statement admitted by <see cref="TryBeginStatement"/>.</summary>
    internal void EndStatement() => EndOperation();

    /// <summary>
    /// Creates the session's refusal of a statement the transaction did not admit
    /// (<see cref="TryBeginStatement"/> returned false): the base's refusal, for the session.
    /// </summary>
    /// <returns>The refusal.</returns>
    internal DatabaseException CreateStatementRefusal() => CreateRefusal();

    internal bool HasStorageFailure => _storageFailure is not null;

    internal Exception RecordStorageFailure(Exception cause)
    {
        _storageFailure ??= cause;
        return cause;
    }

    internal async ValueTask AbortStorageFailureAsync(Exception? cleanupFailure = null)
    {
        var cause = _storageFailure;
        _storageFailure = null;
        if (cleanupFailure is not null) { SqlDatabaseEventSource.Log.TransactionCleanupFailed(this, cleanupFailure); }
        if (cause is null) { return; }
        try { await AbortAsync(cause).ConfigureAwait(false); }
        catch (Exception abortFailure)
        {
            SqlDatabaseEventSource.Log.TransactionCleanupFailed(this, abortFailure);
        }
    }

    /// <inheritdoc />
    protected override TransactionState GetKernelState() => KernelContext.State;

    /// <inheritdoc />
    /// <remarks>
    /// The caller's token is not passed on: under the base a token is observed only before the
    /// commit starts. While the database closes, the kernel refuses every end before it starts: a
    /// commit then fails with <see cref="ObjectDisposedException"/>, and the base reports the
    /// transaction ended with nothing committed. A commit whose record was written but could not be
    /// made durable throws <see cref="DatabaseTransactionCommitUnconfirmedException"/> and leaves the
    /// transaction <c>Committed</c>: the reopen's recovery decides whether it survives (#1243), and
    /// the work must not be retried.
    /// </remarks>
    protected override async ValueTask CommitCoreAsync()
    {
        try
        {
            try { await KernelCoordinator.CommitAsync(KernelContext).ConfigureAwait(false); }
            finally { await ReleaseConfigurationPinAsync().ConfigureAwait(false); }
        }
        catch (Exception error)
        {
            // A kernel abort of the commit crosses the engine boundary as the area root's exception.
            var translated = Translate(error);
            if (ReferenceEquals(translated, error))
            {
                throw;
            }

            throw translated;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller's token is not passed on (PostgreSQL holds interrupts through
    /// <c>AbortTransaction</c> for the same reason), and the kernel ends the context whatever fails
    /// once it starts (#1226). Only a closing database refuses the rollback before it starts, and
    /// its disposal then aborts the context itself.
    /// </remarks>
    protected override async ValueTask RollbackCoreAsync()
    {
        try
        {
            try { await KernelCoordinator.RollbackAsync(KernelContext).ConfigureAwait(false); }
            finally { await ReleaseConfigurationPinAsync().ConfigureAwait(false); }
        }
        catch (Exception error)
        {
            var translated = Translate(error);
            if (ReferenceEquals(translated, error))
            {
                throw;
            }

            throw translated;
        }
    }

    /// <inheritdoc />
    protected override DatabaseException? GetOfflineRefusal() => _database.GetOfflineRefusal();

    /// <inheritdoc />
    protected override DatabaseException CreateAbortedException(Exception? cause, bool commit)
    {
        // The cause travels in the message as well: the wire carries message text, not inner exceptions.
        // The code lives in Diagnostics only; the wire writer adds the prefix (Database DESIGN.md).
        string reason = cause is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, _failureCause, cause.Message);
        string message = (commit ? _commitAborted : _operationsAborted) + reason;
        return new DatabaseException(message, [new Types.Diagnostic
        { Code = AbortedCode, Message = message, Severity = Types.DiagnosticSeverity.Error }], cause);
    }

    // The area error policy: the engine translates the transaction kernel's independent exception
    // root at the model boundary. A failure the offline storage caused becomes the coded refusal
    // (#1243); the unconfirmed commit that took it offline keeps its own type, its message led by
    // the offline code (#1272).
    private Exception Translate(Exception error) => error switch
    {
        TransactionCommitUnconfirmedException unconfirmed => _database.CreateUnconfirmedCommit(unconfirmed),
        _ when _database.TranslateFailure(error) is var offline && !ReferenceEquals(offline, error) => offline,
        TransactionDeadlockException deadlock => SqlExecutionException.Deadlock(deadlock),
        TransactionAbortedException => new DatabaseTransactionAbortedException(error.Message, error),
        _ => error,
    };

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.SqlDatabaseTransaction _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.SqlDatabaseTransaction value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Transactions.TransactionId Id => _target.Id;
        public global::Assimalign.Cohesion.Database.Transactions.IsolationLevel IsolationLevel => _target.IsolationLevel;
    }
}
