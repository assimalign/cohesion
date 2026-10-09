using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client.Internal;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// One typed SQL connection: executes commands against the bound database and materializes their
/// results as typed row sets or affected counts.
/// </summary>
/// <remarks>
/// <para>
/// It wraps one pooled <see cref="DatabaseConnection"/>, maps failures onto the SQL error surface, and
/// fires the client's <see cref="SqlClientObserver"/> around every command.
/// </para>
/// <para>
/// Connections are not thread-safe: one command at a time, mirroring the engine-session contract. A
/// connection rented from a <see cref="SqlClient"/> returns to its pool on dispose (with its
/// authenticated session intact when it is still healthy).
/// </para>
/// <para>
/// Disposal is final for this instance: the pool may rent the session to another caller, so every
/// later command, and a second <see cref="DisposeAsync"/> or <see cref="AbortAsync"/>, is refused or
/// ignored here instead of reaching that caller's rental.
/// </para>
/// </remarks>
public sealed class SqlConnection : IAsyncDisposable
{
    private readonly DatabaseConnection _connection;
    private readonly SqlClientObserver? _observer;
    private int _disposed;

    internal SqlConnection(DatabaseConnection connection, SqlClientObserver? observer)
    {
        _connection = connection;
        _observer = observer;
    }

    /// <summary>
    /// Gets the database this connection is bound to.
    /// </summary>
    public string Database => _connection.Database;

    /// <summary>
    /// Gets a value indicating whether the connection is open and usable. A
    /// statement-level failure leaves the connection open; a protocol or transport
    /// failure marks it broken, and disposal or abort closes it for this instance.
    /// </summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _connection.IsOpen;

    /// <summary>Discards this connection's rental and closes its session. Idempotent, and a no-op after
    /// <see cref="DisposeAsync"/>.</summary>
    /// <returns>The asynchronous transport teardown operation.</returns>
    /// <remarks>Use after an uncertain transaction outcome or failed session cleanup. The connection
    /// is never returned to the pool for reuse. This cannot reverse an already committed transaction
    /// and does not establish the outcome of an unacknowledged COMMIT. This operation is deliberately
    /// not cancellable so an unsafe rental cannot survive cancellation of cleanup.</remarks>
    public async ValueTask AbortAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _connection.AbortAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes a row-returning command and materializes the full result set.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The materialized result set: typed columns and rows.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed or aborted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="SqlClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<SqlResultSet> QueryAsync(SqlCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DatabaseClientResult result = await ExecuteCoreAsync(command.CommandText, command.Parameters.AsWireParameters(), cancellationToken).ConfigureAwait(false);
        return SqlResultSet.FromClientResult(result);
    }

    /// <summary>
    /// Executes a row-returning command from statement text and materializes the
    /// full result set.
    /// </summary>
    /// <param name="commandText">The SQL statement text.</param>
    /// <param name="parameters">Parameter values keyed by bare parameter name, or null when the statement takes none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The materialized result set: typed columns and rows.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="commandText"/> is null or whitespace.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed or aborted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="SqlClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public ValueTask<SqlResultSet> QueryAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        return QueryCoreAsync(commandText, parameters, cancellationToken);
    }

    /// <summary>
    /// Executes a non-row-returning command and returns the number of records it affected.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of records the command affected.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed or aborted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="SqlClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<long> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DatabaseClientResult result = await ExecuteCoreAsync(command.CommandText, command.Parameters.AsWireParameters(), cancellationToken).ConfigureAwait(false);
        return result.AffectedCount;
    }

    /// <summary>
    /// Executes a non-row-returning command from statement text and returns the
    /// number of records it affected.
    /// </summary>
    /// <param name="commandText">The SQL statement text.</param>
    /// <param name="parameters">Parameter values keyed by bare parameter name, or null when the statement takes none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of records the command affected.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="commandText"/> is null or whitespace.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed or aborted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="SqlClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<long> ExecuteAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        DatabaseClientResult result = await ExecuteCoreAsync(commandText, parameters, cancellationToken).ConfigureAwait(false);
        return result.AffectedCount;
    }

    /// <summary>
    /// Executes a command and returns the first column of the first row, or the
    /// type default when the result carries no rows.
    /// </summary>
    /// <typeparam name="T">The expected scalar type.</typeparam>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The first column of the first row cast to <typeparamref name="T"/>, or default.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed or aborted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="SqlClientException">Thrown when the server reports an error, the connection breaks, or the value cannot be cast to <typeparamref name="T"/>.</exception>
    public async ValueTask<T?> ExecuteScalarAsync<T>(SqlCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DatabaseClientResult result = await ExecuteCoreAsync(command.CommandText, command.Parameters.AsWireParameters(), cancellationToken).ConfigureAwait(false);

        if (result.Rows.Count == 0 || result.Columns.Count == 0)
        {
            return default;
        }

        SqlResultSet set = SqlResultSet.FromClientResult(result);
        SqlRow row = set[0];
        return row.IsNull(0) ? default : row.GetFieldValue<T>(0);
    }

    /// <summary>
    /// Returns the connection to its pool, with its authenticated session intact when it is still
    /// healthy. Idempotent, and a no-op after <see cref="AbortAsync"/>.
    /// </summary>
    /// <returns>A task that completes when the rental is returned.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<SqlResultSet> QueryCoreAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        DatabaseClientResult result = await ExecuteCoreAsync(commandText, parameters, cancellationToken).ConfigureAwait(false);
        return SqlResultSet.FromClientResult(result);
    }

    /// <summary>
    /// Runs one command on the shared connection, wrapping it in telemetry and mapping
    /// core failures onto the SQL error surface.
    /// </summary>
    private async ValueTask<DatabaseClientResult> ExecuteCoreAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        // The pooled connection may already serve another caller's rental; never reach it.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        int parameterCount = parameters?.Count ?? 0;
        NotifyExecuting(commandText, parameterCount);

        long startTimestamp = Stopwatch.GetTimestamp();
        SqlClientEventSource.Log.CommandStart(this, parameterCount);

        // The command's end is written on every path, from the finally, before the observer hears
        // of it: CommandFailed and then CommandStop(Error) for a failure, CommandStop(Cancelled)
        // for a cancellation, CommandStop(Success) for a result.
        DatabaseClientResult? result = null;
        SqlClientException? translated = null;
        Exception? failure = null;
        try
        {
            result = await _connection.ExecuteAsync(commandText, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception)
        {
            translated = SqlClientException.FromClientException(exception);
            failure = translated;
        }
        catch (Exception exception) when (SqlClientEventSource.CaptureFailure(exception, out failure))
        {
            // Unreachable: the filter records the failure and declines it.
            throw;
        }
        finally
        {
            SqlClientEventSource.Log.CommandEnded(this, failure, result?.Rows.Count ?? -1, result?.AffectedCount ?? -1, startTimestamp);
        }

        if (translated is not null)
        {
            NotifyFailed(commandText, translated, Stopwatch.GetElapsedTime(startTimestamp));
            throw translated;
        }

        NotifyExecuted(commandText, result!.Rows.Count, result.AffectedCount, Stopwatch.GetElapsedTime(startTimestamp));
        return result;
    }

    private void NotifyExecuting(string commandText, int parameterCount)
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            _observer.OnExecuting(commandText, parameterCount);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A telemetry observer must never fault the command it is observing.
            SqlClientEventSource.Log.ObserverFailed(this, nameof(SqlClientObserver.OnExecuting), exception);
        }
    }

    private void NotifyExecuted(string commandText, long rowCount, long affectedCount, TimeSpan elapsed)
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            _observer.OnExecuted(commandText, rowCount, affectedCount, elapsed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A telemetry observer must never fault the command it is observing.
            SqlClientEventSource.Log.ObserverFailed(this, nameof(SqlClientObserver.OnExecuted), exception);
        }
    }

    private void NotifyFailed(string commandText, SqlClientException exception, TimeSpan elapsed)
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            _observer.OnFailed(commandText, exception, elapsed);
        }
        catch (Exception observerException) when (observerException is not OutOfMemoryException)
        {
            // A telemetry observer must never mask the original failure.
            SqlClientEventSource.Log.ObserverFailed(this, nameof(SqlClientObserver.OnFailed), observerException);
        }
    }
}
