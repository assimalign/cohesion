using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Blob.Client;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>A blob as the browser shows it.</summary>
internal sealed record BlobItem(BlobProperties Properties)
{
    public string Name => Properties.Name;

    public string Summary => $"{Properties.Name}   {FormatLength(Properties.Length)}   {Properties.ContentType ?? "(no type)"}   {Properties.ModifiedAt:yyyy-MM-dd HH:mm:ss}";

    public string Details =>
        $"""
        Name:          {Properties.Name}
        Length:        {Properties.Length:N0} bytes
        Content type:  {Properties.ContentType ?? "(none)"}
        ETag:          {Properties.ETag}
        Checksum:      0x{Properties.Checksum:X8}
        Created:       {Properties.CreatedAt:O}
        Modified:      {Properties.ModifiedAt:O}
        """;

    public static string FormatLength(long length) => length switch
    {
        < 1024 => $"{length} B",
        < 1024 * 1024 => $"{length / 1024.0:F1} KB",
        _ => $"{length / (1024.0 * 1024.0):F1} MB",
    };
}

/// <summary>
/// Blob: <see cref="BlobDatabaseSession"/> (its container operations) embedded, or <see cref="BlobConnection"/>
/// over TCP. The blob wire has no container verbs, so container list/create/drop use an engine
/// session (embedded / loopback only); against an external server the container name is typed in.
/// </summary>
internal sealed class BlobWorkspace : ModelWorkspace
{
    private BlobClient? _client;
    private BlobConnection? _connection;
    private string? _wireDatabase;

    public BlobWorkspace(ConnectionMode mode, StudioEngines engines, EndPoint? wireEndPoint)
        : base(StudioModel.Blob, mode, engines, wireEndPoint)
    {
    }

    protected override string ClientName => "Blob.Client BlobConnection";

    public bool CanManageContainers => Mode != ConnectionMode.WireExternal;

    // The session runs its own container operations (option B, concrete-types plan §6.6); the
    // cast from the root-typed session goes with ModelWorkspace's retype (phase 7).
    private BlobDatabaseSession BlobSession => (BlobDatabaseSession)RequireSession();

    protected override async Task OpenWireAsync(string database, EndPoint endPoint, CancellationToken cancellationToken)
    {
        _client = BlobClient.Create(new BlobClientOptions
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

    private async Task<BlobConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await CloseWireAsync().ConfigureAwait(false);
        await OpenWireAsync(_wireDatabase ?? RequireDatabase(), WireEndPoint!, cancellationToken).ConfigureAwait(false);
        return _connection!;
    }

    /// <summary>Runs <paramref name="action"/> against a blob session (the embedded session or a temporary admin session), whose container operations run in it.</summary>
    private async Task<T> WithBlobSessionAsync<T>(Func<BlobDatabaseSession, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (Mode == ConnectionMode.Embedded)
        {
            return await action(BlobSession, cancellationToken).ConfigureAwait(false);
        }

        DatabaseSession admin = await OpenAdminSessionAsync(cancellationToken).ConfigureAwait(false);
        await using (admin.ConfigureAwait(false))
        {
            return await action((BlobDatabaseSession)admin, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<List<string>> ListContainersAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(token => WithBlobSessionAsync(async (session, inner) =>
        {
            var names = new List<string>();
            await foreach (BlobContainer container in session.GetContainersAsync(inner).ConfigureAwait(false))
            {
                names.Add(container.Name);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }, token), cancellationToken);

    public Task CreateContainerAsync(string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(token => WithBlobSessionAsync(async (session, inner) =>
        {
            await session.CreateContainerAsync(name, inner).ConfigureAwait(false);
            return true;
        }, token), cancellationToken);

    public Task DropContainerAsync(string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(token => WithBlobSessionAsync(async (session, inner) =>
        {
            await session.DropContainerAsync(name, inner).ConfigureAwait(false);
            return true;
        }, token), cancellationToken);

    /// <summary>Container ownership facts (embedded/loopback only; a member of the engine container type).</summary>
    public Task<IReadOnlyDictionary<string, object?>> GetContainerOwnershipAsync(string container, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(token => WithBlobSessionAsync(async (session, inner) =>
        {
            BlobContainer target = await session.GetContainerAsync(container, inner).ConfigureAwait(false);
            return await target.GetOwnershipAsync(inner).ConfigureAwait(false);
        }, token), cancellationToken);

    public Task<List<BlobItem>> ListBlobsAsync(string container, string? prefix, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
            var items = new List<BlobItem>();
            if (Mode == ConnectionMode.Embedded)
            {
                BlobContainer target = await BlobSession.GetContainerAsync(container, token).ConfigureAwait(false);
                await foreach (BlobProperties properties in target.GetBlobsAsync(prefix, token).ConfigureAwait(false))
                {
                    items.Add(new BlobItem(properties));
                }

                return items;
            }

            BlobConnection connection = await ConnectionAsync(token).ConfigureAwait(false);
            await foreach (BlobProperties properties in connection.GetBlobsAsync(container, prefix, token).ConfigureAwait(false))
            {
                items.Add(new BlobItem(properties));
            }

            return items;
        }, cancellationToken);

    public Task<BlobItem?> GetPropertiesAsync(string container, string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            BlobProperties? properties;
            if (Mode == ConnectionMode.Embedded)
            {
                BlobContainer target = await BlobSession.GetContainerAsync(container, token).ConfigureAwait(false);
                properties = await target.GetPropertiesAsync(name, token).ConfigureAwait(false);
            }
            else
            {
                properties = await (await ConnectionAsync(token).ConfigureAwait(false)).GetPropertiesAsync(container, name, token).ConfigureAwait(false);
            }

            return properties is { } found ? new BlobItem(found) : null;
        }, cancellationToken);

    /// <summary>Streams <paramref name="source"/> into the blob; returns the number of bytes written.</summary>
    public Task<long> UploadAsync(string container, string name, Stream source, string contentType, bool overwrite, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode == ConnectionMode.Embedded)
            {
                BlobContainer target = await BlobSession.GetContainerAsync(container, token).ConfigureAwait(false);
                Stream destination = await target.OpenWriteAsync(name, new BlobWriteOptions { ContentType = contentType, Overwrite = overwrite }, token).ConfigureAwait(false);
                long before = source.CanSeek ? source.Position : 0;
                await using (destination.ConfigureAwait(false))
                {
                    await source.CopyToAsync(destination, 81920, token).ConfigureAwait(false);
                }

                return source.CanSeek ? source.Position - before : -1;
            }

            long length = source.CanSeek ? source.Length - source.Position : -1;
            return await (await ConnectionAsync(token).ConfigureAwait(false))
                .UploadAsync(container, name, source, contentType, length, overwrite, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Streams the blob into <paramref name="destination"/>; returns the number of bytes copied.</summary>
    public Task<long> DownloadAsync(string container, string name, Stream destination, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            Stream source;
            if (Mode == ConnectionMode.Embedded)
            {
                BlobContainer target = await BlobSession.GetContainerAsync(container, token).ConfigureAwait(false);
                source = await target.OpenReadAsync(name, token).ConfigureAwait(false);
            }
            else
            {
                source = await (await ConnectionAsync(token).ConfigureAwait(false)).DownloadAsync(container, name, token).ConfigureAwait(false);
            }

            long before = destination.CanSeek ? destination.Position : 0;
            await using (source.ConfigureAwait(false))
            {
                await source.CopyToAsync(destination, 81920, token).ConfigureAwait(false);
            }

            return destination.CanSeek ? destination.Position - before : -1;
        }, cancellationToken);

    public Task<bool> DeleteAsync(string container, string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode == ConnectionMode.Embedded)
            {
                BlobContainer target = await BlobSession.GetContainerAsync(container, token).ConfigureAwait(false);
                return await target.DeleteAsync(name, token).ConfigureAwait(false);
            }

            return await (await ConnectionAsync(token).ConfigureAwait(false)).DeleteAsync(container, name, token).ConfigureAwait(false);
        }, cancellationToken);
}
