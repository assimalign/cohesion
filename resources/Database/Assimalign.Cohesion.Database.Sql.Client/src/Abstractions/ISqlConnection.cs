using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// One typed SQL connection: executes commands against the bound database and
/// materializes their results as typed row sets or affected counts.
/// </summary>
/// <remarks>
/// Connections are not thread-safe — one command at a time, mirroring the
/// engine-session contract. A connection rented from an <see cref="ISqlClient"/>
/// returns to its pool on dispose (with its authenticated session intact when it
/// is still healthy).
/// </remarks>
public interface ISqlConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the database this connection is bound to.
    /// </summary>
    string Database { get; }

    /// <summary>
    /// Gets a value indicating whether the connection is open and usable. A
    /// statement-level failure leaves the connection open; a protocol or transport
    /// failure marks it broken.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Executes a row-returning command and materializes the full result set.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The materialized result set: typed columns and rows.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is null.</exception>
    /// <exception cref="SqlClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    ValueTask<SqlResultSet> QueryAsync(SqlCommand command, CancellationToken cancellationToken = default);

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
    ValueTask<SqlResultSet> QueryAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a non-row-returning command and returns the number of records it affected.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of records the command affected.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is null.</exception>
    /// <exception cref="SqlClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    ValueTask<long> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken = default);

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
    ValueTask<long> ExecuteAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);

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
    ValueTask<T?> ExecuteScalarAsync<T>(SqlCommand command, CancellationToken cancellationToken = default);
}
