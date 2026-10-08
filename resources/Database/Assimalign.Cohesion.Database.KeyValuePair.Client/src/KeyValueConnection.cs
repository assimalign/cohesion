using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.KeyValuePair.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// One typed key-value connection: point operations (get/put/delete/exists) and ordered, bounded
/// scans against the bound database, with per-entry etags for conditional writes.
/// </summary>
/// <remarks>
/// <para>
/// It wraps one pooled <see cref="DatabaseConnection"/>, builds the command grammar
/// (<c>docs/COMMANDS.md</c> in the model package) with byte parameters, decodes the model's result
/// shapes, maps failures onto the key-value error surface, and fires the client's
/// <see cref="KeyValueClientObserver"/> around every command.
/// </para>
/// <para>
/// Connections are not thread-safe: one command at a time, mirroring the engine-session contract. A
/// connection rented from a <see cref="KeyValueClient"/> returns to its pool on dispose (with its
/// authenticated session intact when it is still healthy). Conditional misses (compare-and-swap) are
/// first-class outcomes (<see cref="KeyValueWriteResult"/>, a false return), never exceptions;
/// concurrency conflicts with other transactions surface as <see cref="KeyValueClientException"/>
/// with <see cref="KeyValueClientErrorKind.ExecutionFailure"/> (retryable).
/// </para>
/// <para>
/// Disposal is final for this instance: the pool may rent the session to another caller, so every
/// later command is refused, and a second <see cref="DisposeAsync"/> is ignored, instead of reaching
/// that caller's rental.
/// </para>
/// </remarks>
public sealed class KeyValueConnection : IAsyncDisposable
{
    private readonly DatabaseConnection _connection;
    private readonly KeyValueClientObserver? _observer;
    private int _disposed;

    internal KeyValueConnection(DatabaseConnection connection, KeyValueClientObserver? observer)
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
    /// command-level failure leaves the connection open; a protocol or transport
    /// failure marks it broken, and disposal closes it for this instance.
    /// </summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _connection.IsOpen;

    /// <summary>
    /// Reads the entry for a key.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The entry, or null when the key has no visible entry.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<KeyValueClientEntry?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        KeyValueProtocolResult result = await ExecuteCoreAsync(
            "GET @k",
            new Dictionary<string, object?> { ["k"] = key.ToArray() },
            cancellationToken).ConfigureAwait(false);

        if (result.Rows.Count == 0)
        {
            return null;
        }

        return DecodeEntry("GET @k", result.Rows[0]);
    }

    /// <summary>
    /// Writes the entry for a key unconditionally, inserting or replacing.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The entry's new etag.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error (including a retryable write-write conflict) or the connection breaks mid-exchange.</exception>
    public async ValueTask<long> PutAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        const string command = "PUT @k @v";

        KeyValueProtocolResult result = await ExecuteCoreAsync(
            command,
            new Dictionary<string, object?> { ["k"] = key.ToArray(), ["v"] = value.ToArray() },
            cancellationToken).ConfigureAwait(false);

        KeyValueWriteResult outcome = DecodeWriteOutcome(command, result);

        // An unconditional put always applies (a concurrent conflict surfaces as
        // an ExecutionFailure exception, never as a miss).
        return outcome.ETag ?? throw MalformedResult(command, "an applied write must carry its new etag");
    }

    /// <summary>
    /// Writes the entry for a key under a condition (insert-only or
    /// compare-and-swap). A conditional miss is a first-class outcome, never an
    /// exception.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="condition">The write condition.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The outcome: whether the write applied, and the new (or current) etag.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<KeyValueWriteResult> PutAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, KeyValueWriteCondition condition, CancellationToken cancellationToken = default)
    {
        string command;
        var parameters = new Dictionary<string, object?> { ["k"] = key.ToArray(), ["v"] = value.ToArray() };

        if (condition.OnlyIfAbsent)
        {
            command = "PUT @k @v IF ABSENT";
        }
        else
        {
            command = "PUT @k @v IF @etag";
            parameters["etag"] = condition.ExpectedETag;
        }

        KeyValueProtocolResult result = await ExecuteCoreAsync(command, parameters, cancellationToken).ConfigureAwait(false);
        return DecodeWriteOutcome(command, result);
    }

    /// <summary>
    /// Deletes the entry for a key.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when an entry was deleted; false when none was visible.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<bool> TryDeleteAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        KeyValueProtocolResult result = await ExecuteCoreAsync(
            "DELETE @k",
            new Dictionary<string, object?> { ["k"] = key.ToArray() },
            cancellationToken).ConfigureAwait(false);

        return result.AffectedCount > 0;
    }

    /// <summary>
    /// Deletes the entry for a key only when its current etag matches
    /// (compare-and-swap). A mismatch is a false return, never an exception.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="expectedETag">The etag the current entry must carry.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the entry was deleted; false when none was visible or the etag did not match.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<bool> TryDeleteAsync(ReadOnlyMemory<byte> key, long expectedETag, CancellationToken cancellationToken = default)
    {
        KeyValueProtocolResult result = await ExecuteCoreAsync(
            "DELETE @k IF @etag",
            new Dictionary<string, object?> { ["k"] = key.ToArray(), ["etag"] = expectedETag },
            cancellationToken).ConfigureAwait(false);

        return result.AffectedCount > 0;
    }

    /// <summary>
    /// Probes whether a key has a visible entry.
    /// </summary>
    /// <param name="key">The key to probe.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the key has a visible entry; otherwise false.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<bool> ExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        const string command = "EXISTS @k";

        KeyValueProtocolResult result = await ExecuteCoreAsync(
            command,
            new Dictionary<string, object?> { ["k"] = key.ToArray() },
            cancellationToken).ConfigureAwait(false);

        if (result.Rows.Count != 1 || result.Rows[0] is not [bool exists])
        {
            throw MalformedResult(command, "expected one boolean row");
        }

        return exists;
    }

    /// <summary>
    /// Scans entries in ascending key order, bounded by the given range.
    /// </summary>
    /// <param name="range">Scan bounds and limits, or null to scan the whole key space.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The matching entries in ascending key order, materialized; empty, never null, when nothing matches.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="range"/> combines a prefix with explicit start or end bounds.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection is disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the command is canceled, which marks the connection broken.</exception>
    /// <exception cref="KeyValueClientException">Thrown when the server reports an error or the connection breaks mid-exchange.</exception>
    public async ValueTask<IReadOnlyList<KeyValueClientEntry>> ScanAsync(KeyValueScanRange? range = null, CancellationToken cancellationToken = default)
    {
        var command = "SCAN";
        var parameters = new Dictionary<string, object?>();

        if (range is not null)
        {
            if (range.Prefix is not null && (range.Start is not null || range.End is not null))
            {
                throw new ArgumentException("A prefix scan cannot combine with explicit start/end bounds.", nameof(range));
            }

            if (range.Start is { } start)
            {
                command += " FROM @start";
                parameters["start"] = start.ToArray();
            }

            if (range.End is { } end)
            {
                command += " TO @end";
                parameters["end"] = end.ToArray();
            }

            if (range.Prefix is { } prefix)
            {
                command += " PREFIX @prefix";
                parameters["prefix"] = prefix.ToArray();
            }

            if (range.Limit is { } limit)
            {
                command += " LIMIT @limit";
                parameters["limit"] = limit;
            }
        }

        KeyValueProtocolResult result = await ExecuteCoreAsync(
            command,
            parameters.Count == 0 ? null : parameters,
            cancellationToken).ConfigureAwait(false);

        var entries = new KeyValueClientEntry[result.Rows.Count];

        for (int index = 0; index < result.Rows.Count; index++)
        {
            entries[index] = DecodeEntry(command, result.Rows[index]);
        }

        return entries;
    }

    /// <summary>
    /// Returns the connection to its pool, with its authenticated session intact when it is still
    /// healthy. Idempotent.
    /// </summary>
    /// <returns>A task that completes when the rental is returned.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decodes the model's entry row shape: <c>[key (byte[]), value (byte[]), etag (long)]</c>.
    /// </summary>
    private static KeyValueClientEntry DecodeEntry(string command, object?[] row)
    {
        if (row is not [byte[] key, byte[] value, long etag])
        {
            throw MalformedResult(command, "expected [key, value, etag] rows");
        }

        return new KeyValueClientEntry(key, value, etag);
    }

    /// <summary>
    /// Decodes the model's write outcome shape: one row of
    /// <c>[applied (bool), etag (long or null)]</c>.
    /// </summary>
    private static KeyValueWriteResult DecodeWriteOutcome(string command, KeyValueProtocolResult result)
    {
        if (result.Rows.Count != 1 || result.Rows[0] is not [bool applied, var etag] || etag is not (null or long))
        {
            throw MalformedResult(command, "expected one [applied, etag] outcome row");
        }

        return new KeyValueWriteResult(applied, (long?)etag);
    }

    private static KeyValueClientException MalformedResult(string command, string detail)
        => new(
            KeyValueClientErrorKind.MalformedResult,
            ProtocolErrorCode.Internal,
            $"The server's result for '{command}' does not match the key-value result contract ({detail}).");

    /// <summary>
    /// Runs one command on the shared connection, wrapping it in telemetry and
    /// mapping core failures onto the key-value error surface.
    /// </summary>
    private async ValueTask<KeyValueProtocolResult> ExecuteCoreAsync(string commandText, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        // The pooled connection may already serve another caller's rental; never reach it.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        int parameterCount = parameters?.Count ?? 0;
        NotifyExecuting(commandText, parameterCount);

        long startTimestamp = Stopwatch.GetTimestamp();
        KeyValueClientEventSource.Log.CommandStart(this, parameterCount);

        try
        {
            KeyValueProtocolResult result = await _connection.ExecuteAsync(new KeyValueExecuteExchange(commandText, parameters), cancellationToken).ConfigureAwait(false);

            KeyValueClientEventSource.Log.CommandStop(this, result.Rows.Count, result.AffectedCount, startTimestamp);
            NotifyExecuted(commandText, result.Rows.Count, result.AffectedCount, Stopwatch.GetElapsedTime(startTimestamp));
            return result;
        }
        catch (DatabaseClientException exception)
        {
            KeyValueClientException translated = KeyValueClientException.FromClientException(exception);
            KeyValueClientEventSource.Log.CommandFailed(this, translated, startTimestamp);
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
            KeyValueClientEventSource.Log.ObserverFailed(this, nameof(KeyValueClientObserver.OnExecuting), exception);
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
            KeyValueClientEventSource.Log.ObserverFailed(this, nameof(KeyValueClientObserver.OnExecuted), exception);
        }
    }

    private void NotifyFailed(string commandText, KeyValueClientException exception, TimeSpan elapsed)
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
            KeyValueClientEventSource.Log.ObserverFailed(this, nameof(KeyValueClientObserver.OnFailed), observerException);
        }
    }
}
