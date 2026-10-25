using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// One rental lease over an authenticated protocol session bound to a database and an immutable
/// model message family.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="DatabaseClient.RentAsync(CancellationToken)"/> returns a new lease. Disposing it
/// returns its healthy session to the pool and permanently ends this lease. A stale reference can
/// never use, abort or return a later rental of the session. Repeated disposal or abort awaits the same return.
/// <see cref="IsOpen"/> is the one state or metadata member that does not throw after disposal:
/// it returns <see langword="false"/>. Operations and other metadata members reject this lease permanently.
/// </para>
/// <para>
/// Connections allow one exchange at a time. Disposal cancels and joins an admitted exchange before
/// returning its session, and an overlapping exchange is rejected before it writes any frame.
/// </para>
/// <para>
/// Pool return rolls back an active or failed transaction using negotiated server status. A session
/// that cannot certify a clean state is closed instead of reused (#1540).
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("DatabaseConnection")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Client.DatabaseConnection.DebugView))]
public sealed class DatabaseConnection : IAsyncDisposable
{
    private readonly DatabaseClient _owner;
    private readonly DatabaseConnector _connector;
    // This is the only lease lock. Admission and disposal are serialized here; no pool lock is
    // acquired while holding it. Cancellation callbacks, exchange I/O and pool return run outside
    // it, and return awaits exchange completion before the connector can enter another lease.
    private readonly object _exchangeLock = new();
    private bool _disposed;
    private CancellationTokenSource? _operation;
    private bool _operationCompleting;
    private TaskCompletionSource? _operationCancellationCompletion;
    private TaskCompletionSource? _exchangeCompletion;
    private Task? _returnTask;

    internal DatabaseConnection(DatabaseClient owner, DatabaseConnector connector)
    {
        _owner = owner;
        _connector = connector;
        DatabaseClientEventSource.Log.RentalStarted();
    }

    /// <summary>Gets the database this lease is bound to.</summary>
    /// <exception cref="ObjectDisposedException">The lease is disposed.</exception>
    public string Database
    {
        get
        {
            lock (_exchangeLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _connector.Database;
            }
        }
    }

    /// <summary>Gets the principal this lease authenticated as.</summary>
    /// <exception cref="ObjectDisposedException">The lease is disposed.</exception>
    public string Principal
    {
        get
        {
            lock (_exchangeLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _connector.Principal;
            }
        }
    }

    /// <summary>Gets the protocol version negotiated with the server.</summary>
    /// <exception cref="ObjectDisposedException">The lease is disposed.</exception>
    public ProtocolVersion ServerVersion
    {
        get
        {
            lock (_exchangeLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _connector.ServerVersion;
            }
        }
    }

    /// <summary>Gets whether this lease is undisposed and its session is open and usable.</summary>
    /// <remarks>A complete statement failure preserves health. An unfinished response, transport
    /// failure, cancellation or abort breaks the session. This is the one state or metadata member
    /// that does not throw after disposal; a disposed lease always reports false.</remarks>
    public bool IsOpen
    {
        get
        {
            lock (_exchangeLock)
            {
                return !_disposed && _connector.IsOpen;
            }
        }
    }

    /// <summary>Gets the message family fixed when the owning pool was created.</summary>
    /// <exception cref="ObjectDisposedException">The lease is disposed.</exception>
    public ProtocolMessageFamily Family
    {
        get
        {
            lock (_exchangeLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _connector.Family;
            }
        }
    }

    /// <summary>Gets the capabilities agreed during startup.</summary>
    /// <exception cref="ObjectDisposedException">The lease is disposed.</exception>
    public IReadOnlyList<string> Capabilities
    {
        get
        {
            lock (_exchangeLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _connector.Capabilities;
            }
        }
    }

    /// <summary>Executes one complete model-owned framed exchange.</summary>
    /// <typeparam name="TResult">The model's result type.</typeparam>
    /// <param name="exchange">The operation, bound to this lease's exact family instance.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The model-owned result.</returns>
    /// <exception cref="ArgumentNullException">The exchange is null.</exception>
    /// <exception cref="ArgumentException">The exchange belongs to another family.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The lease is disposed.</exception>
    /// <exception cref="OperationCanceledException">The exchange is canceled, breaking its session.</exception>
    /// <exception cref="DatabaseClientException">The server reports an error or the connection fails.</exception>
    public async ValueTask<TResult> ExecuteAsync<TResult>(DatabaseProtocolExchange<TResult> exchange,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource operation;
        TaskCompletionSource completion;
        lock (_exchangeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(exchange);
            if (!ReferenceEquals(_connector.Family, exchange.Family))
            {
                throw new ArgumentException(ErrorMessages.ClientExecuteStreamingAsyncArgument, nameof(exchange));
            }
            if (!_connector.CanExecute)
            {
                throw new DatabaseClientException(ProtocolErrorCode.Internal, ErrorMessages.ConnectionNotOpen);
            }
            if (_operation is not null)
            {
                throw new InvalidOperationException(ErrorMessages.DatabaseConnectionExecuteAsyncFailure);
            }
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = operation;
            _operationCompleting = false;
            _operationCancellationCompletion = null;
            _exchangeCompletion = completion;
        }

        try
        {
            return await _connector.ExecuteAsync(exchange, operation.Token).ConfigureAwait(false);
        }
        finally
        {
            Task? cancellationCompletion;
            lock (_exchangeLock)
            {
                // Cleanup and return claim source ownership under the same lock. Once cleanup
                // claims it, return may join this exchange but cannot start cancellation later.
                _operationCompleting = true;
                cancellationCompletion = _operationCancellationCompletion?.Task;
            }
            try
            {
                if (cancellationCompletion is not null)
                {
                    // CancelAsync can queue callbacks after marking the token canceled. Keep
                    // their registrations alive until that dispatch has actually completed.
                    await cancellationCompletion.ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    // Disposing a linked source can join a caller's cancellation callback. That
                    // callback may inspect this lease, so never join it under the lease lock.
                    operation.Dispose();
                }
                finally
                {
                    lock (_exchangeLock)
                    {
                        _operation = null;
                        _operationCompleting = false;
                        _operationCancellationCompletion = null;
                        _exchangeCompletion = null;
                        completion.TrySetResult();
                    }
                }
            }
        }
    }

    /// <summary>Starts a bounded download while its framed exchange remains active.</summary>
    /// <param name="exchange">The model operation, bound to this lease's exact family instance.</param>
    /// <param name="cancellationToken">Cancels startup and the returned stream's lifetime.</param>
    /// <returns>A sequential readable stream after the model validates its opening response.</returns>
    /// <exception cref="ArgumentNullException">The exchange is null.</exception>
    /// <exception cref="ArgumentException">The exchange belongs to another family.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="DatabaseClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The lease is disposed.</exception>
    /// <remarks>Dispose the stream. Failed or abandoned transfers return their broken lease.
    /// Verified completion retains this lease for subsequent operations until connection disposal.
    /// Disposing this connection cancels and joins its active exchange.</remarks>
    public ValueTask<Stream> ExecuteStreamingAsync(DatabaseStreamingExchange exchange,
        CancellationToken cancellationToken = default)
    {
        lock (_exchangeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        return DatabaseDownloadStream.CreateAsync(this, exchange, cancellationToken);
    }

    /// <summary>Discards this lease and closes its session instead of pooling it.</summary>
    /// <returns>The noncancellable asynchronous teardown operation.</returns>
    /// <remarks>Cancels and joins an active exchange. On an already-returned lease, awaits the
    /// original return without touching the session. This cannot undo a committed transaction or
    /// establish an unacknowledged command's outcome.</remarks>
    public ValueTask AbortAsync() => ReturnAsync(abort: true);

    /// <summary>Ends this lease, canceling and joining its active exchange before returning its
    /// healthy session to the pool or closing a broken one. Repeated disposal awaits the same return.</summary>
    /// <returns>A task that completes when this lease's session has been returned or closed.</returns>
    public ValueTask DisposeAsync() => ReturnAsync(abort: false);

    private ValueTask ReturnAsync(bool abort)
    {
        TaskCompletionSource returned;
        CancellationTokenSource? operation;
        TaskCompletionSource? cancellationCompletion;
        Task? exchangeCompletion;
        lock (_exchangeLock)
        {
            if (_returnTask is not null)
            {
                return new ValueTask(_returnTask);
            }
            if (abort)
            {
                _connector.Invalidate();
            }
            _disposed = true;
            DatabaseClientEventSource.Log.RentalEnded();
            returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _returnTask = returned.Task;
            exchangeCompletion = _exchangeCompletion?.Task;
            operation = null;
            cancellationCompletion = null;
            if (_operation is not null && !_operationCompleting)
            {
                // Publish the join before requesting cancellation outside the lock. Cleanup
                // must keep the source alive until this particular dispatch has finished.
                operation = _operation;
                cancellationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _operationCancellationCompletion = cancellationCompletion;
            }
        }
        _ = ReturnAfterExchangeAsync(operation, cancellationCompletion, exchangeCompletion, returned);
        return new ValueTask(returned.Task);
    }

    private async Task ReturnAfterExchangeAsync(CancellationTokenSource? operation,
        TaskCompletionSource? cancellationCompletion, Task? completion, TaskCompletionSource returned)
    {
        Exception? failure = null;
        try
        {
            if (operation is not null)
            {
                await operation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // Preserve non-faulting teardown if the operation source was already disposed.
        }
        catch (Exception exception)
        {
            // Exchange-owned cancellation callbacks are an open seam. Even a throwing callback
            // cannot skip joining the exchange or returning its pool slot.
            failure = exception;
        }
        finally
        {
            // The marker only joins dispatch; callback failures belong to return, not to the
            // exchange's original result or cancellation exception.
            cancellationCompletion?.TrySetResult();
        }
        try
        {
            if (completion is not null)
            {
                await completion.ConfigureAwait(false);
            }
            await _owner.ReturnAsync(_connector).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Report teardown failures to every disposer after the pool's finally releases its slot.
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }
        if (failure is null)
        {
            returned.TrySetResult();
        }
        else
        {
            returned.TrySetException(failure);
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Client.DatabaseConnection value)
        {
            _ = value;
        }
    }
}
