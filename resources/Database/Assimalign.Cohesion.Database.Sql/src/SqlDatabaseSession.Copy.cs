using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql;

public sealed partial class SqlDatabaseSession
{
    private CopyOperation? _copyOperation;
    internal Stream? CopyDestination(SqlQueryRequest request)
        => Volatile.Read(ref _copyOperation) is { } operation && ReferenceEquals(operation.Request, request) ? operation.Destination : null;

    /// <summary>Exports one table as UTF-8 text or CSV to a caller-owned destination.</summary>
    /// <param name="statement">A COPY table TO STDOUT statement.</param>
    /// <param name="destination">The writable destination, left open on every outcome; it must honor cancellation.</param>
    /// <param name="cancellationToken">Cancellation token for binding, scanning and delivery.</param>
    /// <returns>The number of exported data rows, excluding an optional header.</returns>
    /// <exception cref="ArgumentException">The statement is blank or the destination is not writable.</exception>
    /// <exception cref="ArgumentNullException">The destination is null.</exception>
    /// <exception cref="InvalidOperationException">A statement, export or transaction control holds the session.</exception>
    /// <exception cref="DatabaseException">Parsing, binding or execution fails, including COPY inside BEGIN.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="Exception">The destination's original exception is rethrown unchanged after cleanup.</exception>
    /// <remarks>Session disposal cancels and waits for the export. The destination must honor
    /// cancellation and must not reenter this session from its write callbacks.</remarks>
    public async ValueTask<long> CopyToAsync(string statement, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfClosed();
        if (!destination.CanWrite) { throw new ArgumentException(ErrorMessages.CopyDestinationInvalid, nameof(destination)); }
        cancellationToken.ThrowIfCancellationRequested();
        var request = SqlQueryRequest.FromSql(statement, null, _parserOptions, _database.Name);
        if (request.Statement.SqlExpression is not SqlCopyToExpression)
        { throw new SqlExecutionException("CS05095", ErrorMessages.CopyStatementRequired); }
        var operation = new CopyOperation(request, destination, cancellationToken);
        bool entered = false;
        try
        {
            if (!TryEnterOperation())
            { throw new InvalidOperationException(Volatile.Read(ref _copyOperation) is null ? ErrorMessages.CopySessionBusy : ErrorMessages.CopyAlreadyActive); }
            entered = true;
            // Publish before Execute's closed-session check. Disposal either cancels and joins
            // this operation, or closes first and Execute refuses before taking a snapshot.
            Volatile.Write(ref _copyOperation, operation);
            var result = await ExecuteAsync(request, operation.Cancellation.Token).ConfigureAwait(false);
            return result.AffectedCount;
        }
        catch (SqlCopyDeliveryException exception) { exception.Rethrow(); throw; }
        finally
        {
            if (entered)
            {
                Volatile.Write(ref _copyOperation, null);
                ExitOperation();
            }
            await operation.CompleteAsync().ConfigureAwait(false);
        }
    }

    // Reuse the existing execution state machine and its finally: ordinary statements gain
    // no wrapper/task/state field. BEGIN and SAVEPOINT already own root admission internally.
    private long EnterStatementExecution(SqlQueryRequest request)
    {
        if (CopyDestination(request) is null)
        {
            if (Volatile.Read(ref _copyOperation) is not null) { throw new InvalidOperationException(ErrorMessages.CopyAlreadyActive); }
            if (request.Statement.SqlExpression is not SqlTransactionExpression && !TryEnterOperation())
            {
                // Preserve an active transaction's more specific recovery/state refusal.
                ThrowIfPartialUndoPending();
                ThrowIfTransactionRefuses();
                throw new InvalidOperationException(ErrorMessages.CopySessionBusy);
            }
        }
        try { return _database.TableReclamation.EnterStatement(); }
        catch { ExitExecution(request); throw; }
    }

    private void ExitExecution(SqlQueryRequest request)
    {
        if (request.Statement.SqlExpression is not SqlTransactionExpression && CopyDestination(request) is null) { ExitOperation(); }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (Volatile.Read(ref _copyOperation) is not { } operation) { return; }
        try { await operation.CancelAsync().ConfigureAwait(false); }
        finally { await operation.Completion.Task.ConfigureAwait(false); }
    }

    [System.Diagnostics.DebuggerDisplay("CopyOperation")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(CopyOperation.DebugView))]
    private sealed class CopyOperation
    {
        private readonly object _sync = new();
        private Task? _canceling;
        private bool _finished;
        internal CopyOperation(SqlQueryRequest request, Stream destination, CancellationToken cancellationToken)
        {
            Request = request;
            Destination = destination;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        internal SqlQueryRequest Request { get; }
        internal Stream Destination { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task CancelAsync()
        {
            // The session monitor is never held here. This short monitor only publishes the
            // cancellation task; no callbacks, transport waits or snapshot work run under it.
            lock (_sync) { return _finished ? Task.CompletedTask : _canceling ??= Cancellation.CancelAsync(); }
        }
        internal async ValueTask CompleteAsync()
        {
            Task? canceling;
            lock (_sync) { _finished = true; canceling = _canceling; }
            try { if (canceling is not null) { await canceling.ConfigureAwait(false); } }
            finally
            {
                Cancellation.Dispose();
                Completion.TrySetResult();
            }
        }
        private sealed class DebugView
        {
            public DebugView(CopyOperation value) { _ = value; }
        }
    }
}
