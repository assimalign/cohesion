using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// The default typed SQL connection: wraps one pooled <see cref="IDatabaseConnection"/>,
/// materializes typed results, maps failures onto the SQL error surface, and drives
/// the telemetry observer.
/// </summary>
internal sealed class SqlConnection : ISqlConnection
{
    private readonly IDatabaseConnection _connection;
    private readonly ISqlClientObserver? _observer;

    internal SqlConnection(IDatabaseConnection connection, ISqlClientObserver? observer)
    {
        _connection = connection;
        _observer = observer;
    }

    /// <inheritdoc />
    public string Database => _connection.Database;

    /// <inheritdoc />
    public bool IsOpen => _connection.IsOpen;

    /// <inheritdoc />
    public async ValueTask<SqlResultSet> QueryAsync(SqlCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DatabaseClientResult result = await ExecuteCoreAsync(command.CommandText, command.Parameters.AsWireParameters(), cancellationToken).ConfigureAwait(false);
        return SqlResultSet.FromClientResult(result);
    }

    /// <inheritdoc />
    public ValueTask<SqlResultSet> QueryAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        return QueryCoreAsync(commandText, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<long> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DatabaseClientResult result = await ExecuteCoreAsync(command.CommandText, command.Parameters.AsWireParameters(), cancellationToken).ConfigureAwait(false);
        return result.AffectedCount;
    }

    /// <inheritdoc />
    public async ValueTask<long> ExecuteAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        DatabaseClientResult result = await ExecuteCoreAsync(commandText, parameters, cancellationToken).ConfigureAwait(false);
        return result.AffectedCount;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
