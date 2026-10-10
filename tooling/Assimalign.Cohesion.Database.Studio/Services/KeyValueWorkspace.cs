using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.KeyValuePair.Client;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>A key-value entry as the browser shows it.</summary>
internal sealed record KeyValueItem(byte[] Key, byte[] Value, long ETag)
{
    public string KeyText => Bytes.ToDisplay(Key);

    public string ValueText
    {
        get
        {
            string text = Bytes.ToDisplay(Value);
            return text.Length > 200 ? text[..197] + "..." : text;
        }
    }

    public string Summary => $"{KeyText}  =  {ValueText}   (etag {ETag}, {Value.Length} B)";
}

/// <summary>Condition for a put.</summary>
internal enum KeyValueCondition
{
    None,
    IfAbsent,
    IfETagMatches,
}

/// <summary>The scan bounds entered on the page.</summary>
internal sealed record KeyValueScan(byte[]? Prefix, byte[]? Start, byte[]? End, int? Limit);

/// <summary>
/// Key-value: typed <see cref="KeyValueRequest"/> commands on an embedded session, or
/// <see cref="KeyValueConnection"/> over TCP. KEYSPACES has no client verb, so it always runs on an
/// engine session (embedded / loopback only).
/// </summary>
internal sealed class KeyValueWorkspace : ModelWorkspace
{
    private KeyValueDatabaseSession? _session;
    private KeyValueClient? _client;
    private KeyValueConnection? _connection;
    private string? _wireDatabase;

    public KeyValueWorkspace(ConnectionMode mode, StudioEngines engines, EndPoint? wireEndPoint)
        : base(StudioModel.KeyValue, mode, engines, wireEndPoint)
    {
    }

    public override KeyValueDatabaseEngine Engine => Engines.KeyValue;

    public override KeyValueDatabaseSession? Session => _session;

    protected override async Task OpenSessionAsync(string database, CancellationToken cancellationToken)
    {
        KeyValueDatabase opened = await Engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        _session = await opened.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override DatabaseSession? DetachSession()
    {
        KeyValueDatabaseSession? session = _session;
        _session = null;
        return session;
    }

    /// <summary>Opens a short-lived session on the current database through the engine (loopback/embedded only).</summary>
    private async Task<KeyValueDatabaseSession> OpenAdminSessionAsync(CancellationToken cancellationToken)
    {
        KeyValueDatabase database = await Engine.OpenDatabaseAsync(RequireAdminDatabase(), cancellationToken).ConfigureAwait(false);
        return await database.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override string ClientName => "KeyValuePair.Client KeyValueConnection";

    protected override async Task OpenWireAsync(string database, EndPoint endPoint, CancellationToken cancellationToken)
    {
        _client = KeyValueClient.Create(new KeyValueClientOptions
        {
            Settings = WireSettings(database, endPoint),
            ConnectionFactory = new TcpConnectionFactory(),
        });
        _wireDatabase = database;
        _connection = await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask CloseWireAsync()
    {
        if (_connection is { } connection)
        {
            _connection = null;
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_client is { } client)
        {
            _client = null;
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<KeyValueConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await CloseWireAsync().ConfigureAwait(false);
        await OpenWireAsync(_wireDatabase ?? RequireDatabase(), WireEndPoint!, cancellationToken).ConfigureAwait(false);
        return _connection!;
    }

    public Task<KeyValueItem?> GetAsync(byte[] key, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode == ConnectionMode.Embedded)
            {
                List<KeyValueItem> entries = await ReadEntriesAsync(new KeyValueGetRequest(key), token).ConfigureAwait(false);
                return entries.Count == 0 ? null : entries[0];
            }

            KeyValueClientEntry? entry = await (await ConnectionAsync(token).ConfigureAwait(false)).GetAsync(key, token).ConfigureAwait(false);
            return entry is { } found ? new KeyValueItem(found.Key.ToArray(), found.Value.ToArray(), found.ETag) : null;
        }, cancellationToken);

    /// <summary>Returns (applied, etag).</summary>
    public Task<(bool Applied, long? ETag)> PutAsync(byte[] key, byte[] value, KeyValueCondition condition, long? expectedETag, CancellationToken cancellationToken = default)
        => RunExclusiveAsync<(bool, long?)>(async token =>
        {
            if (condition == KeyValueCondition.IfETagMatches && expectedETag is null)
            {
                throw new ArgumentException("Enter the expected ETag.");
            }

            if (Mode == ConnectionMode.Embedded)
            {
                var options = condition switch
                {
                    KeyValueCondition.IfAbsent => new KeyValuePutOptions { OnlyIfAbsent = true },
                    KeyValueCondition.IfETagMatches => new KeyValuePutOptions { ExpectedETag = expectedETag },
                    _ => null,
                };

                QueryResult result = await RequireSession().ExecuteAsync(new KeyValuePutRequest(key, value, options), token).ConfigureAwait(false);
                ThrowIfError(result);
                if (result is QueryResultSet set)
                {
                    await using (set.ConfigureAwait(false))
                    {
                        await foreach (QueryRow row in set.GetRowsAsync(token).ConfigureAwait(false))
                        {
                            return (row.GetBoolean(0), row.IsNull(1) ? null : row.GetInt64(1));
                        }
                    }
                }

                return (result.AffectedCount > 0, null);
            }

            KeyValueConnection connection = await ConnectionAsync(token).ConfigureAwait(false);
            if (condition == KeyValueCondition.None)
            {
                long etag = await connection.PutAsync(key, value, token).ConfigureAwait(false);
                return (true, etag);
            }

            KeyValueWriteCondition writeCondition = condition == KeyValueCondition.IfAbsent
                ? KeyValueWriteCondition.IfAbsent
                : KeyValueWriteCondition.IfETagMatches(expectedETag!.Value);
            KeyValueWriteResult write = await connection.PutAsync(key, value, writeCondition, token).ConfigureAwait(false);
            return (write.Applied, write.ETag);
        }, cancellationToken);

    public Task<bool> DeleteAsync(byte[] key, long? expectedETag, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode == ConnectionMode.Embedded)
            {
                QueryResult result = await RequireSession().ExecuteAsync(new KeyValueDeleteRequest(key, expectedETag), token).ConfigureAwait(false);
                ThrowIfError(result);
                if (result is QueryResultSet set)
                {
                    await set.DisposeAsync().ConfigureAwait(false);
                }

                return result.AffectedCount > 0;
            }

            KeyValueConnection connection = await ConnectionAsync(token).ConfigureAwait(false);
            return expectedETag is { } etag
                ? await connection.TryDeleteAsync(key, etag, token).ConfigureAwait(false)
                : await connection.TryDeleteAsync(key, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<bool> ExistsAsync(byte[] key, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode == ConnectionMode.Embedded)
            {
                QueryResult result = await RequireSession().ExecuteAsync(new KeyValueExistsRequest(key), token).ConfigureAwait(false);
                ThrowIfError(result);
                if (result is QueryResultSet set)
                {
                    await using (set.ConfigureAwait(false))
                    {
                        await foreach (QueryRow row in set.GetRowsAsync(token).ConfigureAwait(false))
                        {
                            return row.GetBoolean(0);
                        }
                    }
                }

                return false;
            }

            return await (await ConnectionAsync(token).ConfigureAwait(false)).ExistsAsync(key, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<List<KeyValueItem>> ScanAsync(KeyValueScan scan, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode == ConnectionMode.Embedded)
            {
                var options = new KeyValueScanOptions
                {
                    Prefix = Optional(scan.Prefix),
                    Start = Optional(scan.Start),
                    End = Optional(scan.End),
                    Limit = scan.Limit,
                };
                return await ReadEntriesAsync(new KeyValueScanRequest(options), token).ConfigureAwait(false);
            }

            var range = new KeyValueScanRange
            {
                Prefix = Optional(scan.Prefix),
                Start = Optional(scan.Start),
                End = Optional(scan.End),
                Limit = scan.Limit,
            };
            IReadOnlyList<KeyValueClientEntry> entries = await (await ConnectionAsync(token).ConfigureAwait(false)).ScanAsync(range, token).ConfigureAwait(false);
            return entries.Select(entry => new KeyValueItem(entry.Key.ToArray(), entry.Value.ToArray(), entry.ETag)).ToList();
        }, cancellationToken);

    /// <summary>The KEYSPACES catalog. No client verb exists, so it needs an engine session (not available for an external server).</summary>
    public Task<TabularResult> KeySpacesAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            KeyValueDatabaseSession? admin = null;
            try
            {
                DatabaseSession session = Mode == ConnectionMode.Embedded
                    ? RequireSession()
                    : admin = await OpenAdminSessionAsync(token).ConfigureAwait(false);
                QueryResult result = await session.ExecuteAsync(new KeyValueKeySpacesRequest(), token).ConfigureAwait(false);
                var outcome = new StatementOutcome { Statement = "KEYSPACES" };
                await QueryResultReader.FillAsync(outcome, result, token).ConfigureAwait(false);
                return outcome.Table ?? new TabularResult { Columns = [], ColumnTypes = [] };
            }
            finally
            {
                if (admin is not null)
                {
                    await admin.DisposeAsync().ConfigureAwait(false);
                }
            }
        }, cancellationToken);

    private async Task<List<KeyValueItem>> ReadEntriesAsync(KeyValueRequest request, CancellationToken cancellationToken)
    {
        QueryResult result = await RequireSession().ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        ThrowIfError(result);
        var items = new List<KeyValueItem>();
        if (result is QueryResultSet set)
        {
            await using (set.ConfigureAwait(false))
            {
                await foreach (QueryRow row in set.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    items.Add(new KeyValueItem(row.GetBytes(0).ToArray(), row.GetBytes(1).ToArray(), row.GetInt64(2)));
                }
            }
        }

        return items;
    }

    /// <summary>
    /// A null <c>byte[]</c> converts to an EMPTY <c>ReadOnlyMemory&lt;byte&gt;</c> that still has a value, so
    /// <c>ReadOnlyMemory&lt;byte&gt;? x = (byte[]?)null</c> is not null. Keep "unset" unset.
    /// </summary>
    private static ReadOnlyMemory<byte>? Optional(byte[]? bytes)
        => bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(bytes);

    private static void ThrowIfError(QueryResult result)
    {
        if (result.Status != QueryResultStatus.Success)
        {
            string detail = result.Diagnostics is { Count: > 0 } diagnostics
                ? string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}"))
                : "no diagnostics";
            throw new InvalidOperationException($"Key-value request returned {result.Status}: {detail}");
        }
    }
}
