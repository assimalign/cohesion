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
        IDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ResourceMount> mounts = GetMounts(resource);
        if (mounts.Count == 0)
        {
            return;
        }

        string applicationDirectory = SafeChild(_stateDirectory, application.ToString(), "application");
        string resourceDirectory = SafeChild(applicationDirectory, resource.Name.ToString(), "resource");
        CreatePrivateDirectory(_stateDirectory);
        CreatePrivateDirectory(applicationDirectory);
        CreatePrivateDirectory(resourceDirectory);

        ILocalFileProtector? protector = OperatingSystem.IsWindows()
            ? new WindowsLocalFileProtector(applicationDirectory, application)
            : null;
        var mountVariables = new HashSet<string>(StringComparer.Ordinal);

        foreach (ResourceMount mount in mounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string mountVariable = ResourceEnvironment.Mount(mount.Name);
            if (!mountVariables.Add(mountVariable))
            {
                throw new InvalidDataException(
                    $"Mount '{mount.Name}' collides with another mount after environment-name normalization.");
            }

            string mountPath = SafeChild(resourceDirectory, mount.Name, "mount");
            if (mount.Kind == ResourceMountKind.Volume)
            {
                if (File.Exists(mountPath))
                {
                    throw new IOException($"Mount path '{mountPath}' is a file, but volume mount '{mount.Name}' requires a directory.");
                }

                CreatePrivateDirectory(mountPath);
            }
            else
            {
                if (Directory.Exists(mountPath))
                {
                    throw new IOException($"Mount path '{mountPath}' is a directory, but mount '{mount.Name}' requires a file.");
                }

                byte[] content = GetInitialContent(resource, mount);
                byte[]? persisted = null;
                try
                {
                    persisted = protector is null
                        ? content
                        : protector.Protect(resource.Name.ToString(), mount.Name, content);
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

            environment.TryAdd(mountVariable, mountPath);
        }
    }

    private static IReadOnlyList<ResourceMount> GetMounts(IApplicationResource resource)
    {
        if (resource is IManifestResource manifestResource)
        {
            IReadOnlyList<ResourceManifestMount> declared = manifestResource.Manifest.Mounts;
            var mounts = new ResourceMount[declared.Count];
            for (int index = 0; index < mounts.Length; index++)
            {
                ResourceManifestMount mount = declared[index];
                mounts[index] = new ResourceMount(mount.Name, mount.ContainerPath, mount.Kind);
            }

            return mounts;
        }

        return resource is IMountResource mountResource
            ? mountResource.Mounts
            : Array.Empty<ResourceMount>();
    }

    private static byte[] GetInitialContent(IApplicationResource resource, ResourceMount mount)
    {
        if (resource is not IManifestResource manifestResource)
        {
            return Array.Empty<byte>();
        }

        foreach (ResourceManifestMount declared in manifestResource.Manifest.Mounts)
        {
            if (!string.Equals(declared.Name, mount.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (declared.Source is not null
                && declared.Source.StartsWith(literalPrefix, StringComparison.Ordinal))
            {
                if (mount.Kind != ResourceMountKind.Configuration)
                {
                    throw new InvalidDataException(
                        $"Mount '{mount.Name}' uses a literal source, which is allowed only for Configuration mounts.");
                }

                return Encoding.UTF8.GetBytes(declared.Source[literalPrefix.Length..]);
            }

            if (!string.IsNullOrWhiteSpace(declared.Source))
            {
                throw new NotSupportedException(
                    $"Local resolution of mount source '{declared.Source}' is delivered by design item 25.");
            }

            return Array.Empty<byte>();
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
