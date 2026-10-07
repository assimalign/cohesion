using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

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
/// </remarks>
public sealed class SqlConnection : IAsyncDisposable
{
    private readonly DatabaseConnection _connection;
    private readonly SqlClientObserver? _observer;

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
    /// failure marks it broken.
    /// </summary>
    public bool IsOpen => _connection.IsOpen;

    /// <summary>Discards this connection's rental and closes its session.</summary>
    /// <returns>The asynchronous transport teardown operation.</returns>
    /// <remarks>Use after an uncertain transaction outcome or failed session cleanup. The connection
    /// is never returned to the pool for reuse. This cannot reverse an already committed transaction
    /// and does not establish the outcome of an unacknowledged COMMIT. This operation is deliberately
    /// not cancellable so an unsafe rental cannot survive cancellation of cleanup.</remarks>
    public ValueTask AbortAsync() => _connection.AbortAsync();

    /// <summary>
    /// Executes a row-returning command and materializes the full result set.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The materialized result set: typed columns and rows.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is null.</exception>
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
    /// healthy.
    /// </summary>
    /// <returns>A task that completes when the rental is returned.</returns>
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

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
        NotifyExecuting(commandText, parameters?.Count ?? 0);

        long startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            DatabaseClientResult result = await _connection.ExecuteAsync(commandText, parameters, cancellationToken).ConfigureAwait(false);

            NotifyExecuted(commandText, result.Rows.Count, result.AffectedCount, Stopwatch.GetElapsedTime(startTimestamp));
            return result;
        }
        catch (DatabaseClientException exception)
        {
            SqlClientException translated = SqlClientException.FromClientException(exception);
            NotifyFailed(commandText, translated, Stopwatch.GetElapsedTime(startTimestamp));
            throw translated;
        }
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
        }
    }
}
