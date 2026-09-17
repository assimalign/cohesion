using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalProcessStateStore
{
    private readonly string _stateDirectory;

    public LocalProcessStateStore(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public async Task<LocalFileLease> AcquireApplicationLeaseAsync(
        ApplicationName application,
        string owner,
        bool adopt,
        CancellationToken cancellationToken)
    {
        string applicationDirectory = GetApplicationDirectory(application);
        Directory.CreateDirectory(applicationDirectory);
        LocalFileLease lease = await LocalFileLease.AcquireAsync(
                Path.Combine(applicationDirectory, "gateway.lock"),
                waitForAvailability: false,
                $"Application '{application}' is already supervised by another local gateway. " +
                "Stop that gateway before starting or uninstalling this application.",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await InitializeOwnerAsync(
                    application,
                    applicationDirectory,
                    owner,
                    adopt,
                    cancellationToken)
                .ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static async Task InitializeOwnerAsync(
        ApplicationName application,
        string applicationDirectory,
        string owner,
        bool adopt,
        CancellationToken cancellationToken)
    {
        string ownerPath = Path.Combine(applicationDirectory, "owner");
        string? observedOwner = File.Exists(ownerPath)
            ? (await File.ReadAllTextAsync(ownerPath, cancellationToken).ConfigureAwait(false)).Trim()
            : null;

        if (!string.IsNullOrWhiteSpace(observedOwner)
            && !string.Equals(observedOwner, owner, StringComparison.Ordinal)
            && !adopt)
        {
            throw new InvalidOperationException(
                $"Target is owned by '{observedOwner}', but this gateway expects '{owner}'. " +
                "Refusing to take ownership; pass --adopt to adopt the existing target explicitly.");
        }

        if (!string.Equals(observedOwner, owner, StringComparison.Ordinal))
        {
            await WriteTextAtomicallyAsync(ownerPath, owner, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<LocalProcessRegistration?> LoadAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken)
    {
        string path = GetProcessPath(application, resource);
        using LocalFileLease lease = await AcquireProcessLeaseAsync(path, cancellationToken)
            .ConfigureAwait(false);
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
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        ApplicationName application,
        ResourceName resource,
        LocalProcessRegistration registration,
        CancellationToken cancellationToken)
    {
        string path = GetProcessPath(application, resource);
        using LocalFileLease lease = await AcquireProcessLeaseAsync(path, cancellationToken)
            .ConfigureAwait(false);
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
        }
    }

    public async Task DeleteIfMatchesAsync(
        ApplicationName application,
        ResourceName resource,
        LocalProcessRegistration registration,
        CancellationToken cancellationToken)
    {
        string path = GetProcessPath(application, resource);
        using LocalFileLease lease = await AcquireProcessLeaseAsync(path, cancellationToken)
            .ConfigureAwait(false);
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

            if (current is not null && RegistrationsMatch(current, registration))
            {
                File.Delete(path);
            }
        }
        catch (FileNotFoundException)
        {
        }
    }

    private static bool RegistrationsMatch(
        LocalProcessRegistration current,
        LocalProcessRegistration expected)
    {
        if (current.RegistrationId != Guid.Empty || expected.RegistrationId != Guid.Empty)
        {
            return current.RegistrationId != Guid.Empty
                && current.RegistrationId == expected.RegistrationId;
        }

        return current.ProcessId == expected.ProcessId
            && current.StartTimeUtcTicks == expected.StartTimeUtcTicks
            && string.Equals(current.ExecutablePath, expected.ExecutablePath, StringComparison.Ordinal)
            && current.HasProcessGroup == expected.HasProcessGroup
            && string.Equals(current.StopEventName, expected.StopEventName, StringComparison.Ordinal);
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

    private static Task<LocalFileLease> AcquireProcessLeaseAsync(
        string processPath,
        CancellationToken cancellationToken) =>
        LocalFileLease.AcquireAsync(
            processPath + ".lock",
            waitForAvailability: true,
            $"Process registration '{processPath}' is currently locked.",
            cancellationToken);

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

}

internal sealed class LocalProcessRegistration
{
    public Guid RegistrationId { get; init; }

    public int ProcessId { get; init; }

    public long StartTimeUtcTicks { get; init; }

    public string ExecutablePath { get; init; } = string.Empty;

    public bool HasProcessGroup { get; init; }

    public string? StopEventName { get; init; }
}
