using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Binds a virtual mount prefix to a concrete <see cref="IFileSystem"/>. Mounts are stored on
/// the aggregate's mount table and consulted via longest-prefix matching when a request comes in.
/// </summary>
internal sealed class AggregateMount
{
    /// <summary>
    /// The absolute virtual path under which <see cref="FileSystem"/> is exposed. Stored in
    /// canonical form (rooted at "/", no trailing separator except the bare root case).
    /// </summary>
    public FileSystemPath MountPath { get; }

    /// <summary>The mounted provider.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>
    /// When <see langword="true"/> the aggregate disposes <see cref="FileSystem"/> as part of
    /// its own <see cref="IDisposable.Dispose"/>. When <see langword="false"/> the caller retains
    /// ownership of the underlying provider's lifetime.
    /// </summary>
    public bool OwnsFileSystem { get; }

    public AggregateMount(FileSystemPath mountPath, IFileSystem fileSystem, bool ownsFileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        MountPath = Normalize(mountPath);
        FileSystem = fileSystem;
        OwnsFileSystem = ownsFileSystem;
    }

    /// <summary>
    /// Translates an absolute aggregate-side path into the relative path consumed by the
    /// mounted provider. The mount root maps to "/" on the provider; anything deeper has the
    /// mount prefix stripped.
    /// </summary>
    public FileSystemPath ToProviderPath(FileSystemPath absolutePath)
    {
        string mountText = MountPath.ToString();
        string text = absolutePath.ToString();

        if (mountText == "/" || mountText.Length == 0)
        {
            return absolutePath;
        }

        if (text.Length == mountText.Length)
        {
            // Asking for the mount root itself.
            return "/";
        }

        // Strip "{mountText}/" so "/data/foo/bar.txt" becomes "/foo/bar.txt".
        return text.Substring(mountText.Length);
    }

    /// <summary>
    /// Translates a provider-relative path back into the aggregate-side virtual path. Inverse
    /// of <see cref="ToProviderPath"/>.
    /// </summary>
    public FileSystemPath ToAggregatePath(FileSystemPath providerPath)
    {
        string mountText = MountPath.ToString();
        string text = providerPath.ToString();

        if (mountText == "/" || mountText.Length == 0)
        {
            return providerPath;
        }

        if (string.IsNullOrEmpty(text) || text == "/")
        {
            return MountPath;
        }

        // Provider paths are rooted at "/"; concatenate with the mount prefix.
        return text[0] == '/' ? mountText + text : mountText + "/" + text;
    }

    /// <summary>
    /// Normalizes <paramref name="path"/> so the mount table compares apples to apples:
    /// rooted at '/', no trailing separator (except the bare root).
    /// </summary>
    private static FileSystemPath Normalize(FileSystemPath path)
    {
        string text = path.ToString();
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Mount path cannot be empty.", nameof(path));
        }

        if (text[0] != '/')
        {
            text = "/" + text;
        }

        // Strip trailing separator unless the path IS the bare root.
        if (text.Length > 1 && text[text.Length - 1] == '/')
        {
            text = text.Substring(0, text.Length - 1);
        }

        return FileSystemPath.Parse(text);
    }
}
