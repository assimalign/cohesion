using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Keeps deployment records as files under a gateway's state directory, for gateways whose resources read the
/// gateway's own file system: Local and in-process.
/// </summary>
/// <remarks>
/// <para>
/// A resource's record is the directory <c>&lt;state&gt;/&lt;application&gt;/&lt;resource&gt;/.state/record/</c>, holding
/// only layer files, so instances read it directly as <see cref="AppEnvironment.Variables.ConfigDirectory"/>. The
/// generation is kept beside
/// it in <c>record.json</c>, written after the layers, so an interrupted write is redone by the next deployment.
/// Each file is replaced atomically, and an instance accepts a read only when two consecutive reads agree, so it
/// never applies a record half written.
/// </para>
/// <para>
/// One gateway process writes a state directory; writes for one resource are serialized within it.
/// </para>
/// </remarks>
[DebuggerDisplay("{StateDirectory,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class FileResourceRecordStore : IResourceRecordStore
{
    private const string stateDirectoryName = ".state";
    private const string recordDirectoryName = "record";
    private const string metadataFileName = "record.json";
    private const string layerFileExtension = ".json";
    // A record whose generation file is missing never matches a computed generation, so the next deployment rewrites it.
    private const string unknownGeneration = "unknown";
    private const UnixFileMode privateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode privateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    private FileResourceRecordStore(string stateDirectory)
    {
        StateDirectory = Path.GetFullPath(stateDirectory);
    }

    /// <summary>Gets the gateway state directory the store keeps records under.</summary>
    public string StateDirectory { get; }

    /// <summary>Creates a store that keeps records under a gateway state directory.</summary>
    /// <param name="stateDirectory">The gateway state directory, normally <c>.cohesion</c>.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ArgumentException"><paramref name="stateDirectory"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stateDirectory"/> is null.</exception>
    public static FileResourceRecordStore Create(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        return new FileResourceRecordStore(stateDirectory);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The application or resource name cannot be used as a directory name.</exception>
    public string GetInstanceDirectory(ApplicationName application, ResourceName resource) =>
        Path.Combine(GetResourceStateDirectory(application, resource), recordDirectoryName);

    /// <inheritdoc />
    public async ValueTask<ResourceRecord?> ReadAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = GetGate(application, resource);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Read(application, resource);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public async ValueTask<bool> TryWriteAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceRecord record,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        SemaphoreSlim gate = GetGate(application, resource);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedVersion is not null &&
                !string.Equals(Read(application, resource)?.Version, expectedVersion, StringComparison.Ordinal))
            {
                return false;
            }

            string state = GetResourceStateDirectory(application, resource);
            string directory = Path.Combine(state, recordDirectoryName);
            CreatePrivateDirectory(state);
            CreatePrivateDirectory(directory);
            await WriteLayerAsync(state, directory, ResourceConfigurationRecord.DeploymentLayerFileName, record.Deployment, cancellationToken)
                .ConfigureAwait(false);
            foreach (string file in Directory.EnumerateFiles(directory, ResourceConfigurationRecord.ClaimLayerFilePrefix + "*" + layerFileExtension))
            {
                if (!TryGetOwner(file, out ApplicationName owner) || !record.Claims.ContainsKey(owner))
                {
                    File.Delete(file);
                }
            }

            foreach ((ApplicationName owner, ReadOnlyMemory<byte> layer) in record.Claims)
            {
                await WriteLayerAsync(state, directory, ClaimFileName(owner), layer, cancellationToken).ConfigureAwait(false);
            }

            await WriteLayerAsync(state, directory, ResourceConfigurationRecord.OverrideLayerFileName, record.Override, cancellationToken)
                .ConfigureAwait(false);
            await WriteFileAsync(state, Path.Combine(state, metadataFileName), WriteMetadata(record.Generation), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = GetGate(application, resource);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string state = GetResourceStateDirectory(application, resource);
            string directory = Path.Combine(state, recordDirectoryName);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            string metadata = Path.Combine(state, metadataFileName);
            if (File.Exists(metadata))
            {
                File.Delete(metadata);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private ResourceRecord? Read(ApplicationName application, ResourceName resource)
    {
        string state = GetResourceStateDirectory(application, resource);
        string directory = Path.Combine(state, recordDirectoryName);
        string metadata = Path.Combine(state, metadataFileName);
        if (!File.Exists(metadata) && !Directory.Exists(directory))
        {
            return null;
        }

        var claims = new Dictionary<ApplicationName, ReadOnlyMemory<byte>>();
        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory, ResourceConfigurationRecord.ClaimLayerFilePrefix + "*" + layerFileExtension))
            {
                if (TryGetOwner(file, out ApplicationName owner))
                {
                    claims.Add(owner, File.ReadAllBytes(file));
                }
            }
        }

        return ResourceRecord.Create(
            ReadGeneration(metadata),
            ReadLayer(Path.Combine(directory, ResourceConfigurationRecord.DeploymentLayerFileName)),
            claims,
            ReadLayer(Path.Combine(directory, ResourceConfigurationRecord.OverrideLayerFileName)));
    }

    private static byte[] ReadLayer(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();

    private static string ReadGeneration(string metadata)
    {
        if (!File.Exists(metadata))
        {
            return unknownGeneration;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(metadata));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("generation", out JsonElement generation) &&
                generation.ValueKind == JsonValueKind.String &&
                generation.GetString() is { Length: > 0 } value
                    ? value
                    : unknownGeneration;
        }
        catch (JsonException)
        {
            return unknownGeneration;
        }
    }

    private static byte[] WriteMetadata(string generation)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("generation", generation);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    // An unchanged layer is left alone, so an instance's poll sees no change; an empty one is removed.
    private static async Task WriteLayerAsync(
        string state,
        string directory,
        string fileName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, fileName);
        if (content.IsEmpty)
        {
            File.Delete(path);
            return;
        }

        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content.Span))
        {
            return;
        }

        await WriteFileAsync(state, path, content, cancellationToken).ConfigureAwait(false);
    }

    // The temporary file is written outside the record directory, so an instance never lists it.
    private static async Task WriteFileAsync(string state, string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(state, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = privateFileMode;
            }

            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string ClaimFileName(ApplicationName owner) =>
        ResourceConfigurationRecord.ClaimLayerFilePrefix + owner.ToString() + layerFileExtension;

    private static bool TryGetOwner(string file, out ApplicationName owner)
    {
        string name = Path.GetFileName(file);
        string candidate = name[ResourceConfigurationRecord.ClaimLayerFilePrefix.Length..^layerFileExtension.Length];
        if (ApplicationName.IsValid(candidate, out _))
        {
            owner = candidate;
            return true;
        }

        owner = default;
        return false;
    }

    private string GetResourceStateDirectory(ApplicationName application, ResourceName resource)
    {
        string applicationDirectory = SafeChild(StateDirectory, application.ToString());
        string resourceDirectory = SafeChild(applicationDirectory, resource.ToString());
        return Path.Combine(resourceDirectory, stateDirectoryName);
    }

    private SemaphoreSlim GetGate(ApplicationName application, ResourceName resource) =>
        _gates.GetOrAdd(application.ToString() + "/" + resource.ToString(), static _ => new SemaphoreSlim(1, 1));

    private static string SafeChild(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException(ErrorMessages.ResourceRecordPathInvalid);
        }

        string child = Path.GetFullPath(Path.Combine(parent, name));
        string prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return child.StartsWith(prefix, comparison) && child.Length > prefix.Length
            ? child
            : throw new InvalidDataException(ErrorMessages.ResourceRecordPathInvalid);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, privateDirectoryMode);
    }

    private sealed class DebugView
    {
        private readonly FileResourceRecordStore _target;

        public DebugView(FileResourceRecordStore value)
        {
            _target = value;
        }

        public string StateDirectory => _target.StateDirectory;
    }
}
