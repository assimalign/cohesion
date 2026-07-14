using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// The default typed key-value connection: wraps one pooled
/// <see cref="IDatabaseConnection"/>, builds the command grammar
/// (<c>docs/COMMANDS.md</c> in the model package) with byte parameters, decodes
/// the model's result shapes, maps failures onto the key-value error surface,
/// and drives the telemetry observer.
/// </summary>
internal sealed class KeyValueConnection : IKeyValueConnection
{
    private readonly IDatabaseConnection _connection;
    private readonly IKeyValueClientObserver? _observer;

    internal KeyValueConnection(IDatabaseConnection connection, IKeyValueClientObserver? observer)
    {
        _connection = connection;
        _observer = observer;
    }

    /// <inheritdoc />
    public string Database => _connection.Database;

    /// <inheritdoc />
    public bool IsOpen => _connection.IsOpen;

    /// <inheritdoc />
    public async ValueTask<KeyValueClientEntry?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        DatabaseClientResult result = await ExecuteCoreAsync(
            "GET @k",
            new Dictionary<string, object?> { ["k"] = key.ToArray() },
            cancellationToken).ConfigureAwait(false);

        if (result.Rows.Count == 0)
        {
            return null;
        }

        return DecodeEntry("GET @k", result.Rows[0]);
    }

    /// <inheritdoc />
    public async ValueTask<long> PutAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        const string command = "PUT @k @v";

        DatabaseClientResult result = await ExecuteCoreAsync(
            command,
            new Dictionary<string, object?> { ["k"] = key.ToArray(), ["v"] = value.ToArray() },
            cancellationToken).ConfigureAwait(false);

        KeyValueWriteResult outcome = DecodeWriteOutcome(command, result);

        // An unconditional put always applies (a concurrent conflict surfaces as
        // an ExecutionFailure exception, never as a miss).
        return outcome.ETag ?? throw MalformedResult(command, "an applied write must carry its new etag");
    }

    /// <inheritdoc />
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

        DatabaseClientResult result = await ExecuteCoreAsync(command, parameters, cancellationToken).ConfigureAwait(false);
        return DecodeWriteOutcome(command, result);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryDeleteAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        DatabaseClientResult result = await ExecuteCoreAsync(
            "DELETE @k",
            new Dictionary<string, object?> { ["k"] = key.ToArray() },
            cancellationToken).ConfigureAwait(false);

        return result.AffectedCount > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryDeleteAsync(ReadOnlyMemory<byte> key, long expectedETag, CancellationToken cancellationToken = default)
    {
        DatabaseClientResult result = await ExecuteCoreAsync(
            "DELETE @k IF @etag",
            new Dictionary<string, object?> { ["k"] = key.ToArray(), ["etag"] = expectedETag },
            cancellationToken).ConfigureAwait(false);

        return result.AffectedCount > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        const string command = "EXISTS @k";

        DatabaseClientResult result = await ExecuteCoreAsync(
            command,
            new Dictionary<string, object?> { ["k"] = key.ToArray() },
            cancellationToken).ConfigureAwait(false);

        if (result.Rows.Count != 1 || result.Rows[0] is not [bool exists])
        {
            throw MalformedResult(command, "expected one boolean row");
        }

        return exists;
    }

    /// <inheritdoc />
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

        DatabaseClientResult result = await ExecuteCoreAsync(
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

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

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
    private static KeyValueWriteResult DecodeWriteOutcome(string command, DatabaseClientResult result)
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
            KeyValueClientException translated = KeyValueClientException.FromClientException(exception);
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
        }
    }
}
