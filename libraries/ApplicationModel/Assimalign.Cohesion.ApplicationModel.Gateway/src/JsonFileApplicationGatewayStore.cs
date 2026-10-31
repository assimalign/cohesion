using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Keeps each gateway's record in a JSON file, one file per gateway in one directory.
/// </summary>
/// <remarks>
/// <para>
/// A save writes a temporary file and then replaces the gateway's file with it, so a crash leaves
/// the previous record or the new one, never a torn one. Snapshot values are written as base64,
/// whatever content serializer produced them.
/// </para>
/// <para>
/// Every read and write of a gateway's record holds an exclusive lock on the gateway's lock file,
/// <c>{gateway}.lock</c> beside its record. Taking the record over and saving it are therefore atomic
/// across processes, and a read never meets a file being replaced. The operating system releases the
/// lock when its process ends, so a gateway that crashes never leaves it held.
/// </para>
/// <para>
/// The store suits development and gateways on one machine. A platform package backs
/// <see cref="IApplicationGatewayStore"/> with durable storage of its own.
/// </para>
/// </remarks>
public sealed class JsonFileApplicationGatewayStore : IApplicationGatewayStore
{
    // How often an operation tries a held lock again, and how long it tries before it reports the
    // lock file's error. The lock is held only while one record is read or written.
    private static readonly TimeSpan _lockRetryInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(30);

    private JsonFileApplicationGatewayStore(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    /// <summary>
    /// Gets the full path of the directory that holds the records.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Creates a store that keeps its records in a directory, created on the first save.
    /// </summary>
    /// <param name="directoryPath">The directory's path, absolute or relative to the current directory.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static JsonFileApplicationGatewayStore Create(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException(ErrorMessages.ApplicationGatewayStoreDirectoryInvalid, nameof(directoryPath));
        }

        return new JsonFileApplicationGatewayStore(Path.GetFullPath(directoryPath));
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="gateway"/> is <see langword="default"/>.</exception>
    /// <exception cref="JsonException">The gateway's file is not a record.</exception>
    /// <exception cref="IOException">Another process held the gateway's lock for 30 seconds.</exception>
    public async ValueTask<ApplicationGatewayRecord?> LoadAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        string path = GetPath(gateway, ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream gate = await LockAsync(gateway, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="gateway"/> is <see langword="default"/>.</exception>
    /// <exception cref="JsonException">The gateway's file is not a record.</exception>
    /// <exception cref="IOException">Another process held the gateway's lock for 30 seconds.</exception>
    public async ValueTask<ApplicationGatewayRecord> AcquireAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        string path = GetPath(gateway, ".json");
        Directory.CreateDirectory(DirectoryPath);

        await using FileStream gate = await LockAsync(gateway, cancellationToken).ConfigureAwait(false);
        ApplicationGatewayRecord current = await ReadAsync(path, cancellationToken).ConfigureAwait(false) ?? new ApplicationGatewayRecord(gateway, []);
        ApplicationGatewayRecord acquired = current with { Epoch = current.Epoch + 1 };
        await WriteAsync(path, acquired, cancellationToken).ConfigureAwait(false);
        return acquired;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The record's gateway is <see langword="default"/>.</exception>
    /// <exception cref="JsonException">The gateway's file is not a record.</exception>
    /// <exception cref="IOException">Another process held the gateway's lock for 30 seconds.</exception>
    public async ValueTask<bool> SaveAsync(ApplicationGatewayRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        string path = GetPath(record.Gateway, ".json");
        Directory.CreateDirectory(DirectoryPath);

        await using FileStream gate = await LockAsync(record.Gateway, cancellationToken).ConfigureAwait(false);
        if (await ReadAsync(path, cancellationToken).ConfigureAwait(false) is { } stored && stored.Epoch > record.Epoch)
        {
            // A later run took the record over.
            return false;
        }

        await WriteAsync(path, record, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Opens the gateway's lock file with no sharing, which every platform .NET runs on enforces as an
    // exclusive lock between processes and within one; disposing the stream releases it.
    private async Task<FileStream> LockAsync(GatewayName gateway, CancellationToken cancellationToken)
    {
        string path = GetPath(gateway, ".lock");
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < _lockTimeout)
            {
                // Another operation holds the lock for as long as it reads or writes the record.
                await Task.Delay(_lockRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<ApplicationGatewayRecord?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(path);
        ApplicationGatewayRecord? record = await JsonSerializer.DeserializeAsync(stream, ApplicationGatewayStoreJsonContext.Default.ApplicationGatewayRecord, cancellationToken).ConfigureAwait(false);
        return record ?? throw new JsonException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationGatewayRecordNull, path));
    }

    // Writes a temporary file and replaces the record with it, so a crash leaves the previous record
    // or the new one, never a torn one.
    private static async Task WriteAsync(string path, ApplicationGatewayRecord record, CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, record, ApplicationGatewayStoreJsonContext.Default.ApplicationGatewayRecord, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    // Gateway names are lowercase letters, digits, and hyphens, so each is a safe file name.
    private string GetPath(GatewayName gateway, string extension)
    {
        if (gateway.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(gateway));
        }

        return Path.Combine(DirectoryPath, gateway.Value + extension);
    }
}
