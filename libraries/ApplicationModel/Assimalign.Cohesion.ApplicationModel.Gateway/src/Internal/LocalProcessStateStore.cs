using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalProcessStateStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _stateDirectory;

    public LocalProcessStateStore(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public async Task InitializeAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        string applicationDirectory = GetApplicationDirectory(model.Name);
        Directory.CreateDirectory(applicationDirectory);
        string ownerPath = Path.Combine(applicationDirectory, "owner");
        string? observedOwner = File.Exists(ownerPath)
            ? (await File.ReadAllTextAsync(ownerPath, cancellationToken).ConfigureAwait(false)).Trim()
            : null;

        model.AssertOwner(observedOwner);
        if (!string.Equals(observedOwner, model.Owner, StringComparison.Ordinal))
        {
            await WriteTextAtomicallyAsync(ownerPath, model.Owner, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<LocalProcessRegistration?> LoadAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken)
    {
        string path = GetProcessPath(application, resource);
        SemaphoreSlim gate = GetPathGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using FileStream stream = File.OpenRead(path);
            LocalProcessRegistration? registration = await JsonSerializer.DeserializeAsync(
                stream,
                LocalGatewayJsonContext.Default.LocalProcessRegistration,
                cancellationToken).ConfigureAwait(false);

            return registration ?? throw new InvalidDataException($"Process identity file '{path}' is empty.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        ApplicationName application,
        ResourceName resource,
        LocalProcessRegistration registration,
        CancellationToken cancellationToken)
    {
        string path = GetProcessPath(application, resource);
        SemaphoreSlim gate = GetPathGate(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    registration,
                    LocalGatewayJsonContext.Default.LocalProcessRegistration,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            gate.Release();
        }
    }

    public async Task DeleteIfMatchesAsync(
        ApplicationName application,
        ResourceName resource,
        LocalProcessRegistration registration)
    {
        string path = GetProcessPath(application, resource);
        SemaphoreSlim gate = GetPathGate(path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            LocalProcessRegistration? current;
            try
            {
                await using (FileStream stream = File.OpenRead(path))
                {
                    current = await JsonSerializer.DeserializeAsync(
                        stream,
                        LocalGatewayJsonContext.Default.LocalProcessRegistration).ConfigureAwait(false);
                }
            }
            catch (FileNotFoundException)
            {
                return;
            }

            if (current is not null
                && current.ProcessId == registration.ProcessId
                && current.StartTimeUtcTicks == registration.StartTimeUtcTicks)
            {
                File.Delete(path);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void DeleteStale(ApplicationName application, ResourceName resource)
    {
        string path = GetProcessPath(application, resource);
        SemaphoreSlim gate = GetPathGate(path);
        gate.Wait();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetApplicationDirectory(ApplicationName application)
    {
        string applicationDirectory = GetChildDirectory(
            _stateDirectory,
            application.ToString(),
            "Application");
        return GetChildDirectory(applicationDirectory, ".state", "Gateway metadata");
    }

    private string GetProcessPath(ApplicationName application, ResourceName resource)
    {
        string applicationDirectory = GetApplicationDirectory(application);
        string resourceDirectory = GetChildDirectory(applicationDirectory, resource.ToString(), "Resource");
        return Path.Combine(resourceDirectory, "pid");
    }

    private static string GetChildDirectory(string parent, string child, string kind)
    {
        string directory = Path.GetFullPath(Path.Combine(parent, child));
        string root = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!directory.StartsWith(root, comparison))
        {
            throw new InvalidDataException($"{kind} name '{child}' cannot be used as a state directory.");
        }

        return directory;
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                value + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SemaphoreSlim GetPathGate(string path)
        => PathGates.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));
}

internal sealed class LocalProcessRegistration
{
    public int ProcessId { get; init; }

    public long StartTimeUtcTicks { get; init; }

    public string ExecutablePath { get; init; } = string.Empty;

    public bool HasProcessGroup { get; init; }

    public string? StopEventName { get; init; }
}
