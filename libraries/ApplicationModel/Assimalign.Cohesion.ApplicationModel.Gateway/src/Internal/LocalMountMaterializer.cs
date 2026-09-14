using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalMountMaterializer
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _stateDirectory;

    public LocalMountMaterializer(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public async Task MaterializeAsync(
        ApplicationName application,
        IApplicationResource resource,
        ResourcePlan plan,
        ResourceInputs inputs,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inputs);

        IReadOnlyList<MountBinding> mounts = plan.Container.Mounts;
        bool isComposite = string.Equals(plan.Kind, "Composite", StringComparison.OrdinalIgnoreCase);
        var mountVariables = new string[mounts.Count];
        var uniqueMountVariables = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < mounts.Count; index++)
        {
            MountBinding mount = mounts[index];
            string environmentMountName = isComposite
                ? $"{resource.Name}-{mount.Mount}"
                : mount.Mount;
            string mountVariable = ResourceEnvironment.Mount(environmentMountName);
            if (!uniqueMountVariables.Add(mountVariable))
            {
                throw new InvalidDataException(
                    $"Mount '{mount.Mount}' collides with another mount after environment-name normalization.");
            }

            mountVariables[index] = mountVariable;
        }

        string applicationDirectory = SafeChild(_stateDirectory, application.ToString(), "application");
        string resourceDirectory = SafeChild(applicationDirectory, resource.Name.ToString(), "resource");
        CreatePrivateDirectory(_stateDirectory);
        CreatePrivateDirectory(applicationDirectory);
        CreatePrivateDirectory(resourceDirectory);

        ILocalFileProtector? protector = OperatingSystem.IsWindows()
            ? new WindowsLocalFileProtector(
                SafeChild(applicationDirectory, ".state", "gateway metadata"),
                application)
            : null;

        await MaterializeBootstrapCredentialAsync(
            resourceDirectory,
            resource.Name,
            inputs.BootstrapCredential,
            protector,
            environment,
            cancellationToken).ConfigureAwait(false);

        await MaterializeTrustBundleAsync(application, resource.Name, inputs.TrustBundle, environment, cancellationToken).ConfigureAwait(false);

        for (int index = 0; index < mounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MountBinding mount = mounts[index];
            string mountPath = SafeChild(resourceDirectory, mount.Mount, "mount");
            if (mount.Kind == ResourceMountKind.Volume)
            {
                if (File.Exists(mountPath))
                {
                    throw new IOException($"Mount path '{mountPath}' is a file, but volume mount '{mount.Mount}' requires a directory.");
                }

                CreatePrivateDirectory(mountPath);
            }
            else
            {
                if (Directory.Exists(mountPath))
                {
                    throw new IOException($"Mount path '{mountPath}' is a directory, but mount '{mount.Mount}' requires a file.");
                }

                if (!inputs.Mounts.TryGetValue(mount.Mount, out ResourceMountInput? input))
                {
                    throw new InvalidOperationException(
                        $"Resource '{resource.Name}' has no input for mount '{mount.Mount}'.");
                }

                if (!input.IsResolved)
                {
                    throw new InvalidOperationException(
                        input.UnresolvedReason
                        ?? $"Mount '{mount.Mount}' on resource '{resource.Name}' is unresolved.");
                }

                byte[] content = input.Content.ToArray();
                byte[]? persisted = null;
                try
                {
                    persisted = protector is null
                        ? content
                        : protector.Protect(resource.Name.ToString(), mount.Mount, content);
                    await WriteFileAsync(mountPath, persisted, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                    if (persisted is not null && !ReferenceEquals(content, persisted))
                    {
                        CryptographicOperations.ZeroMemory(persisted);
                    }
                }
            }

            if (isComposite)
            {
                GatewayEnvironmentVariables.Remove(
                    environment,
                    ResourceEnvironment.Mount(mount.Mount));
            }

            GatewayEnvironmentVariables.Set(environment, mountVariables[index], mountPath);
        }
    }

    public Task DeleteAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string applicationDirectory = SafeChild(_stateDirectory, application.ToString(), "application");
        string resourceDirectory = SafeChild(applicationDirectory, resource.ToString(), "resource");
        if (Directory.Exists(resourceDirectory))
        {
            Directory.Delete(resourceDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal async Task MaterializeTrustBundleAsync(ApplicationName application, ResourceName resource,
        ReadOnlyMemory<byte> trustBundle, IDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        string applicationDirectory = SafeChild(_stateDirectory, application.ToString(), "application");
        string resourceDirectory = SafeChild(applicationDirectory, resource.ToString(), "resource");
        ILocalFileProtector? protector = OperatingSystem.IsWindows()
            ? new WindowsLocalFileProtector(Path.Combine(applicationDirectory, ".state"), application)
            : null;
        await MaterializeBootstrapCredentialAsync(resourceDirectory, resource, trustBundle, protector, environment,
            cancellationToken, "trust.pem", ResourceEnvironment.TrustBundlePath).ConfigureAwait(false);
    }

    private static string SafeChild(string parent, string name, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string parentPath = Path.GetFullPath(parent);
        string childPath = Path.GetFullPath(Path.Combine(parentPath, name));
        string prefix = parentPath.EndsWith(Path.DirectorySeparatorChar)
            ? parentPath
            : parentPath + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!childPath.StartsWith(prefix, comparison))
        {
            throw new InvalidDataException($"The {kind} name '{name}' cannot be used as a local mount path.");
        }

        return childPath;
    }

    private static async Task WriteFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
                options.UnixCreateMode = PrivateFileMode;
            }

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, PrivateFileMode);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task MaterializeBootstrapCredentialAsync(
        string resourceDirectory,
        ResourceName resource,
        ReadOnlyMemory<byte> credential,
        ILocalFileProtector? protector,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken,
        string fileName = "bootstrap.token",
        string variable = ResourceEnvironment.BootstrapTokenPath)
    {
        string stateDirectory = SafeChild(resourceDirectory, ".state", "resource state");
        string credentialPath = SafeChild(
            stateDirectory,
            fileName,
            "bootstrap credential");

        if (credential.IsEmpty)
        {
            GatewayEnvironmentVariables.Remove(
                environment,
                variable);
            if (File.Exists(credentialPath))
            {
                File.Delete(credentialPath);
            }

            return;
        }

        CreatePrivateDirectory(stateDirectory);
        byte[] content = credential.ToArray();
        byte[]? persisted = null;
        try
        {
            persisted = protector is null
                ? content
                : protector.Protect(resource.ToString(), fileName, content);
            await WriteFileAsync(credentialPath, persisted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            if (persisted is not null && !ReferenceEquals(content, persisted))
            {
                CryptographicOperations.ZeroMemory(persisted);
            }
        }

        GatewayEnvironmentVariables.Set(
            environment,
            variable,
            credentialPath);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, PrivateDirectoryMode);
        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }
}
