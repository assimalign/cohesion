using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalMountMaterializer
{
    private const string literalPrefix = "literal:";
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
        IDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        IReadOnlyList<MountBinding> mounts = plan.Container.Mounts;
        if (mounts.Count == 0)
        {
            return;
        }

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

                byte[] content = GetInitialContent(mount);
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

    private static byte[] GetInitialContent(MountBinding mount)
    {
        if (mount.Source is not null
            && mount.Source.StartsWith(literalPrefix, StringComparison.Ordinal))
        {
            if (mount.Kind != ResourceMountKind.Configuration)
            {
                throw new InvalidDataException(
                    $"Mount '{mount.Mount}' uses a literal source, which is allowed only for Configuration mounts.");
            }

            return Encoding.UTF8.GetBytes(mount.Source[literalPrefix.Length..]);
        }

        if (!string.IsNullOrWhiteSpace(mount.Source))
        {
            throw new NotSupportedException(
                $"Local resolution of mount source '{mount.Source}' is delivered by design item 25.");
        }

        return Array.Empty<byte>();
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
