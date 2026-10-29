using System;
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
/// The store suits development and a single gateway process. It is the placeholder for a durable
/// store over the Database area, which replaces it behind <see cref="IApplicationGatewayStore"/>.
/// </para>
/// </remarks>
public sealed class JsonFileApplicationGatewayStore : IApplicationGatewayStore
{
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
    public async ValueTask<ApplicationGatewayRecord?> LoadAsync(GatewayName gateway, CancellationToken cancellationToken = default)
    {
        string path = GetPath(gateway);
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(path);
        ApplicationGatewayRecord? record = await JsonSerializer.DeserializeAsync(stream, ApplicationGatewayStoreJsonContext.Default.ApplicationGatewayRecord, cancellationToken).ConfigureAwait(false);
        return record ?? throw new JsonException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationGatewayRecordNull, path));
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The record's gateway is <see langword="default"/>.</exception>
    public async ValueTask SaveAsync(ApplicationGatewayRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        string path = GetPath(record.Gateway);
        string temporary = path + ".tmp";
        Directory.CreateDirectory(DirectoryPath);

        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, record, ApplicationGatewayStoreJsonContext.Default.ApplicationGatewayRecord, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    // Gateway names are lowercase letters, digits, and hyphens, so each is a safe file name.
    private string GetPath(GatewayName gateway)
    {
        if (gateway.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(gateway));
        }

        return Path.Combine(DirectoryPath, gateway.Value + ".json");
    }
}
