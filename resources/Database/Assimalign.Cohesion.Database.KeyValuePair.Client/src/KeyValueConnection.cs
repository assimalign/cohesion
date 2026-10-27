using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.KeyValuePair.Client.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

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
[global::System.Diagnostics.DebuggerDisplay("KeyValueConnection {Database,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.Client.KeyValueConnection.DebugView))]
public sealed class KeyValueConnection : IAsyncDisposable
{
    private readonly DatabaseConnection _connection;
    // Operation diagnostics may finish after disposal has returned the shared lease.
    private readonly string _database;
    private readonly KeyValueClientObserver? _observer;
    private int _disposed;

    internal KeyValueConnection(DatabaseConnection connection, KeyValueClientObserver? observer)
    {
        _connection = connection;
        _database = connection.Database;
        _observer = observer;
    }

    /// <summary>
    /// Gets the database this connection is bound to.
    /// </summary>
    public string Database => _database;

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
        return outcome.ETag ?? throw MalformedResult(command, ErrorMessages.ValueConnectionPutAsyncKeyValueClient);
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
            throw MalformedResult(command, ErrorMessages.ConnectionExistsAsyncKeyValueClient);
        }

        return exists;
    }

    /// <summary>
    /// Scans entries in ascending key order, bounded by the given range.
    /// </summary>
    /// <param name="range">Scan bounds and limits, or null to scan the whole key space.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The matching entries in ascending key order, materialized; empty, never null, when nothing matches.</returns>
    /// <exception cref="ArgumentException">Thrown when the returned task is awaited if <paramref name="range"/> combines a non-empty prefix with non-empty start or end bounds.</exception>
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
            if (range.Prefix is { IsEmpty: false } && (range.Start is { IsEmpty: false } || range.End is { IsEmpty: false }))
            {
                throw new ArgumentException(ErrorMessages.KeyValueConnectionScanAsyncArgument, nameof(range));
            }

            if (range.Start is { IsEmpty: false } start)
            {
                command += " FROM @start";
                parameters["start"] = start.ToArray();
            }

            if (range.End is { IsEmpty: false } end)
            {
                command += " TO @end";
                parameters["end"] = end.ToArray();
            }

            if (range.Prefix is { IsEmpty: false } prefix)
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

    /// <summary>Invokes a registered scalar over the existing Execute/result frames.</summary>
    /// <param name="name">The function identifier, matched ignoring case; default is refused.</param>
    /// <param name="arguments">Native primitive arguments in order; NULL is permitted and binary bytes are copied.</param>
    /// <param name="cancellationToken">Cancellation before encoding preserves this token and leaves the rental untouched; cancellation of an admitted exchange follows existing connection policy.</param>
    /// <returns>The declared primitive value or null; binary results are caller-owned byte arrays.</returns>
    /// <exception cref="ArgumentNullException">The argument list is null.</exception>
    /// <exception cref="ArgumentException">The name is default or an argument is not a supported primitive.</exception>
    /// <exception cref="ObjectDisposedException">This rental was disposed.</exception>
    /// <exception cref="InvalidOperationException">Another command is active on the connection.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled before encoding (retaining its token), or an admitted exchange was canceled.</exception>
    /// <exception cref="KeyValueClientException">The server refused the call or returned a malformed scalar result.</exception>
    public async ValueTask<object?> InvokeFunctionAsync(KeyValueFunctionName name, IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (name.Value is null) { throw new ArgumentException(ErrorMessages.FunctionNameRequired, nameof(name)); }
        ArgumentNullException.ThrowIfNull(arguments);
        // This call is not admitted yet: preserve the caller's token before the shared
        // exchange creates a linked operation token or any parameter bytes are encoded.
        cancellationToken.ThrowIfCancellationRequested();
        var command = new StringBuilder("CALL ").Append(name.ToString());
        var parameters = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index++)
        {
            string parameter = "p" + index.ToString(CultureInfo.InvariantCulture);
            object? value = arguments[index];
            if (!TryGetFunctionType(value, out _))
            {
                throw new ArgumentException(ErrorMessages.FunctionArgumentUnsupported, nameof(arguments));
            }
            parameters.Add(parameter, value switch
            {
                byte[] bytes => bytes.AsSpan().ToArray(),
                ReadOnlyMemory<byte> bytes => bytes.ToArray(),
                Memory<byte> bytes => bytes.ToArray(),
                _ => value,
            });
            command.Append(" @").Append(parameter);
        }
        string text = command.ToString();
        KeyValueProtocolResult result = await ExecuteCoreAsync(text, parameters, cancellationToken).ConfigureAwait(false);
        if (result.Columns.Count != 1 || result.Columns[0].Name != "value"
            || (byte)result.Columns[0].Type > (byte)DatabaseType.Guid
            || result.AffectedCount != -1 || result.Rows.Count != 1 || result.Rows[0].Length != 1
            || !TryGetFunctionType(result.Rows[0][0], out DatabaseType actual)
            || result.Rows[0][0] is not null && actual != result.Columns[0].Type)
        {
            throw MalformedResult(text, ErrorMessages.FunctionResultInvalid);
        }
        return result.Rows[0][0] is byte[] binary ? binary.AsSpan().ToArray() : result.Rows[0][0];
    }

    private static bool TryGetFunctionType(object? value, out DatabaseType type)
    {
        type = value switch
        {
            null => DatabaseType.Null,
            bool => DatabaseType.Boolean,
            sbyte => DatabaseType.Int8,
            short => DatabaseType.Int16,
            int => DatabaseType.Int32,
            long => DatabaseType.Int64,
            float => DatabaseType.Float32,
            double => DatabaseType.Float64,
            decimal => DatabaseType.Decimal,
            string => DatabaseType.String,
            byte[] or ReadOnlyMemory<byte> or Memory<byte> => DatabaseType.Binary,
            DateOnly => DatabaseType.Date,
            TimeOnly => DatabaseType.Time,
            DateTime => DatabaseType.DateTime,
            DateTimeOffset => DatabaseType.DateTimeOffset,
            TimeSpan => DatabaseType.TimeSpan,
            Guid => DatabaseType.Guid,
            _ => (DatabaseType)byte.MaxValue,
        };
        return (byte)type <= (byte)DatabaseType.Guid;
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
            throw MalformedResult(command, ErrorMessages.ConnectionDecodeEntryKeyValueClient);
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
            throw MalformedResult(command, ErrorMessages.DecodeWriteOutcomeKeyValueClient);
        }

        return new KeyValueWriteResult(applied, (long?)etag);
    }

    private static KeyValueClientException MalformedResult(string command, string detail)
        => new(
            KeyValueClientErrorKind.MalformedResult,
            ProtocolErrorCode.Internal,
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.MalformedResultKeyValueClient, command, detail));

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
        bool startWritten = KeyValueClientEventSource.Log.CommandStart(this, parameterCount);

        // The command's end is written on every path, from the finally, before the observer hears
        // of it: CommandFailed and then CommandStop(Error) for a failure, CommandStop(Cancelled)
        // for a cancellation, CommandStop(Success) for a result.
        KeyValueProtocolResult? result = null;
        KeyValueClientException? translated = null;
        Exception? failure = null;
        try
        {
            result = await _connection.ExecuteAsync(new KeyValueExecuteExchange(commandText, parameters), cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception)
        {
            translated = KeyValueClientException.FromClientException(exception);
            failure = translated;
        }
        catch (Exception exception) when (KeyValueClientEventSource.CaptureFailure(exception, out failure))
        {
            // Unreachable: the filter records the failure and declines it.
            throw;
        }
        finally
        {
            KeyValueClientEventSource.Log.CommandEnded(this, startWritten, failure, result?.Rows.Count ?? -1, result?.AffectedCount ?? -1, startTimestamp);
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

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.KeyValuePair.Client.KeyValueConnection _target;

        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.Client.KeyValueConnection value)
        {
            _target = value;
        }

        public string Database => _target.Database;
    }
}
