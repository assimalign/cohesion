using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// <see cref="IFileSystem"/> implementation that aggregates multiple mounted providers under a
/// single virtual namespace. Path resolution uses longest-prefix matching against the mount
/// table, so the request "/data/cache/foo" is routed to the mount whose path is the longest
/// prefix of "/data/cache/foo".
/// </summary>
/// <remarks>
/// <para>
/// Intermediate path segments that don't correspond to any mount root surface as synthetic
/// read-only directories. For example, with only "/data/cache" mounted, "/data" is synthetic:
/// it exists for enumeration / traversal, but every mutating call returns
/// <see cref="FileSystemException"/> with code <see cref="FileSystemErrorCode.ReadOnly"/>.
/// </para>
/// <para>
/// <see cref="CopyFile"/> and <see cref="Move"/> work transparently across mount boundaries by
/// streaming the source into a freshly created destination on the target provider.
/// </para>
/// </remarks>
[DebuggerDisplay("{Name} [mounts={MountCount}]")]
public sealed class AggregateFileSystem : IFileSystem
{
    private readonly string _name;
    private readonly bool _isReadOnly;
    private readonly List<AggregateMount> _mountsSorted;
    private readonly IFileSystemDirectory _rootDirectory;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new aggregate. Mounts are sorted by descending path length at construction
    /// so subsequent routing is a single forward scan.
    /// </summary>
    public AggregateFileSystem(AggregateFileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _name = options.Name ?? nameof(AggregateFileSystem);
        _isReadOnly = options.IsReadOnly;
        _mountsSorted = AggregateRouter.SortByLongestPrefix(options.Mounts);
        _rootDirectory = BuildRootDirectory();
    }

    /// <inheritdoc />
    public string Name { get { CheckIfDisposed(); return _name; } }

    /// <inheritdoc />
    public bool IsReadOnly { get { CheckIfDisposed(); return _isReadOnly; } }

    /// <inheritdoc />
    public Size Size
    {
        get
        {
            CheckIfDisposed();
            long total = 0;
            foreach (var mount in _mountsSorted) { total += mount.FileSystem.Size.Length; }
            return total;
        }
    }

    /// <inheritdoc />
    public Size SpaceAvailable
    {
        get
        {
            CheckIfDisposed();
            long total = 0;
            foreach (var mount in _mountsSorted) { total += mount.FileSystem.SpaceAvailable.Length; }
            return total;
        }
    }

    /// <inheritdoc />
    public Size SpaceUsed
    {
        get
        {
            CheckIfDisposed();
            long total = 0;
            foreach (var mount in _mountsSorted) { total += mount.FileSystem.SpaceUsed.Length; }
            return total;
        }
    }

    /// <inheritdoc />
    public IFileSystemDirectory RootDirectory { get { CheckIfDisposed(); return _rootDirectory; } }

    /// <summary>
    /// Internal mount count used by the debugger display string and tests.
    /// </summary>
    internal int MountCount => _mountsSorted.Count;

    /// <summary>
    /// Sorted view of the mount table — provided for the aggregate's wrappers and the event
    /// token, never exposed publicly.
    /// </summary>
    internal IReadOnlyList<AggregateMount> Mounts => _mountsSorted;

    /// <inheritdoc />
    public bool Exists(FileSystemPath path)
    {
        CheckIfDisposed();
        FileSystemPath absolute = Normalize(path);

        var mount = AggregateRouter.Resolve(_mountsSorted, absolute);
        if (mount is not null)
        {
            return mount.FileSystem.Exists(mount.ToProviderPath(absolute));
        }

        // Synthetic intermediate directories also "exist" — they're not backed by a provider but
        // they're reachable through traversal.
        return absolute.ToString() == "/"
            || AggregateRouter.IsSyntheticIntermediate(_mountsSorted, absolute);
    }

    /// <inheritdoc />
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateDirectory));

        FileSystemPath absolute = Normalize(path);
        var mount = AggregateRouter.Resolve(_mountsSorted, absolute);
        if (mount is null)
        {
            // The path is in synthetic space — no provider can fulfill the create, and we don't
            // synthesize new mounts on the fly.
            FileSystemException.ThrowReadOnly(nameof(CreateDirectory));
        }

        var providerDir = mount!.FileSystem.CreateDirectory(mount.ToProviderPath(absolute));
        return new AggregateFileSystemDirectory(this, mount, providerDir, absolute);
    }

    /// <inheritdoc />
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateFile));

        FileSystemPath absolute = Normalize(path);
        var mount = AggregateRouter.Resolve(_mountsSorted, absolute);
        if (mount is null)
        {
            FileSystemException.ThrowReadOnly(nameof(CreateFile));
        }

        var providerFile = mount!.FileSystem.CreateFile(mount.ToProviderPath(absolute));
        return new AggregateFileSystemFile(this, mount, providerFile, absolute);
    }

    /// <inheritdoc />
    public void DeleteDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteDirectory));

        FileSystemPath absolute = Normalize(path);
        var mount = AggregateRouter.Resolve(_mountsSorted, absolute);
        if (mount is null)
        {
            FileSystemException.ThrowDirectoryNotFound(absolute);
        }

        mount!.FileSystem.DeleteDirectory(mount.ToProviderPath(absolute));
    }

    /// <inheritdoc />
    public void DeleteFile(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteFile));

        FileSystemPath absolute = Normalize(path);
        var mount = AggregateRouter.Resolve(_mountsSorted, absolute);
        if (mount is null)
        {
            FileSystemException.ThrowFileNotFound(absolute);
        }

        mount!.FileSystem.DeleteFile(mount.ToProviderPath(absolute));
    }

    /// <inheritdoc />
    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        FileSystemPath absolute = Normalize(path);

        var resolved = TryResolveDirectory(absolute);
        if (resolved is null)
        {
            FileSystemException.ThrowDirectoryNotFound(absolute);
        }
        return resolved!;
    }

    /// <inheritdoc />
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        CheckIfDisposed();
        FileSystemPath absolute = Normalize(path);

        var mount = AggregateRouter.Resolve(_mountsSorted, absolute);
        if (mount is null)
        {
            FileSystemException.ThrowFileNotFound(absolute);
        }

        var providerFile = mount!.FileSystem.GetFile(mount.ToProviderPath(absolute));
        return new AggregateFileSystemFile(this, mount, providerFile, absolute);
    }

    /// <inheritdoc />
    public IFileSystemInfo GetInfo(FileSystemPath path)
    {
        CheckIfDisposed();
        FileSystemPath absolute = Normalize(path);

        var mount = AggregateRouter.Resolve(_mountsSorted, absolute);
        if (mount is not null)
        {
            var providerInfo = mount.FileSystem.GetInfo(mount.ToProviderPath(absolute));
            return providerInfo switch
            {
                IFileSystemFile file => new AggregateFileSystemFile(this, mount, file, absolute),
                IFileSystemDirectory dir => new AggregateFileSystemDirectory(this, mount, dir, absolute),
                _ => providerInfo,
            };
        }

        if (absolute.ToString() == "/" || AggregateRouter.IsSyntheticIntermediate(_mountsSorted, absolute))
        {
            return new AggregateSyntheticDirectory(this, absolute);
        }

        FileSystemException.ThrowPathNotFound(absolute);
        return default!;
    }

    /// <inheritdoc />
    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CopyFile));

        FileSystemPath sourceAbs = Normalize(source);
        FileSystemPath destAbs = Normalize(destination);

        var sourceMount = AggregateRouter.Resolve(_mountsSorted, sourceAbs);
        var destMount = AggregateRouter.Resolve(_mountsSorted, destAbs);

        if (sourceMount is null)
        {
            FileSystemException.ThrowFileNotFound(sourceAbs);
        }
        if (destMount is null)
        {
            FileSystemException.ThrowReadOnly(nameof(CopyFile));
        }

        if (ReferenceEquals(sourceMount!.FileSystem, destMount!.FileSystem))
        {
            // Same provider — delegate so it can optimize and so any provider-specific behavior
            // (timestamps, attributes) is preserved.
            sourceMount.FileSystem.CopyFile(
                sourceMount.ToProviderPath(sourceAbs),
                destMount.ToProviderPath(destAbs));
            return;
        }

        // Cross-provider copy — stream the bytes.
        var sourceFile = sourceMount.FileSystem.GetFile(sourceMount.ToProviderPath(sourceAbs));
        var destFile = destMount.FileSystem.CreateFile(destMount.ToProviderPath(destAbs));
        StreamContents(sourceFile, destFile);
    }

    /// <inheritdoc />
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(Move));

        FileSystemPath sourceAbs = Normalize(source);
        FileSystemPath destAbs = Normalize(destination);

        var sourceMount = AggregateRouter.Resolve(_mountsSorted, sourceAbs);
        var destMount = AggregateRouter.Resolve(_mountsSorted, destAbs);

        if (sourceMount is null)
        {
            FileSystemException.ThrowPathNotFound(sourceAbs);
        }
        if (destMount is null)
        {
            FileSystemException.ThrowReadOnly(nameof(Move));
        }

        if (ReferenceEquals(sourceMount!.FileSystem, destMount!.FileSystem))
        {
            sourceMount.FileSystem.Move(
                sourceMount.ToProviderPath(sourceAbs),
                destMount.ToProviderPath(destAbs));
            return;
        }

        // Cross-provider move — copy then delete the source. If the destination create or copy
        // fails, the source is left in place so callers can retry without losing data.
        var providerSourcePath = sourceMount.ToProviderPath(sourceAbs);
        var sourceFile = sourceMount.FileSystem.GetFile(providerSourcePath);
        var destFile = destMount.FileSystem.CreateFile(destMount.ToProviderPath(destAbs));
        StreamContents(sourceFile, destFile);
        sourceMount.FileSystem.DeleteFile(providerSourcePath);
    }

    /// <inheritdoc />
    public IFileSystemEventToken Watch(Glob? pattern)
    {
        CheckIfDisposed();
        return new AggregateFileSystemEventToken(_mountsSorted, pattern);
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        CheckIfDisposed();
        return _rootDirectory.EnumerateFileSystem(options);
    }

    /// <inheritdoc />
    public IEnumerator<IFileSystemInfo> GetEnumerator()
        => EnumerateFileSystem(new FileSystemEnumerationOptions { Recurse = true }).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) { return; }
        _isDisposed = true;

        foreach (var mount in _mountsSorted)
        {
            if (mount.OwnsFileSystem)
            {
                try { mount.FileSystem.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) { return; }
        _isDisposed = true;

        foreach (var mount in _mountsSorted)
        {
            if (mount.OwnsFileSystem)
            {
                try { await mount.FileSystem.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // Internal helpers used by the directory wrappers + synthetic dirs.
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the aggregate-side parent for the root of <paramref name="mount"/>. When the
    /// mount is at "/", returns null. Otherwise, the parent is the synthetic directory at the
    /// path obtained by stripping the last segment of the mount path.
    /// </summary>
    internal IFileSystemDirectory? GetParentForMountRoot(AggregateMount mount)
    {
        string mountText = mount.MountPath.ToString();
        if (mountText == "/" || string.IsNullOrEmpty(mountText))
        {
            return null;
        }
        int lastSep = mountText.LastIndexOf('/');
        FileSystemPath parentPath = lastSep <= 0 ? "/" : mountText.Substring(0, lastSep);
        return ResolveDirectory(parentPath);
    }

    /// <summary>
    /// Resolves <paramref name="absolute"/> to a directory. Returns a wrapped mounted directory,
    /// a synthetic directory for intermediate / root segments, or <see langword="null"/> if the
    /// path is in neither category.
    /// </summary>
    internal IFileSystemDirectory? TryResolveDirectory(FileSystemPath absolute)
    {
        var mount = AggregateRouter.Resolve(_mountsSorted, absolute);
        if (mount is not null)
        {
            try
            {
                var providerDir = mount.FileSystem.GetDirectory(mount.ToProviderPath(absolute));
                return new AggregateFileSystemDirectory(this, mount, providerDir, absolute);
            }
            catch (FileSystemException ex) when (ex.Code == FileSystemErrorCode.NotFound)
            {
                return null;
            }
        }

        if (absolute.ToString() == "/" || AggregateRouter.IsSyntheticIntermediate(_mountsSorted, absolute))
        {
            return new AggregateSyntheticDirectory(this, absolute);
        }

        return null;
    }

    /// <summary>
    /// Same as <see cref="TryResolveDirectory"/> but throws <see cref="FileSystemException"/>
    /// with code <see cref="FileSystemErrorCode.NotFound"/> instead of returning null.
    /// </summary>
    internal IFileSystemDirectory ResolveDirectory(FileSystemPath absolute)
    {
        var result = TryResolveDirectory(absolute);
        if (result is null)
        {
            FileSystemException.ThrowDirectoryNotFound(absolute);
        }
        return result!;
    }

    /// <summary>
    /// Returns the immediate synthetic children below <paramref name="absolute"/>. Used by the
    /// synthetic directory's <see cref="IFileSystemDirectory.GetDirectories"/> implementation.
    /// </summary>
    internal IEnumerable<string> GetSyntheticChildren(FileSystemPath absolute)
        => AggregateRouter.SyntheticChildren(_mountsSorted, absolute);

    /// <summary>
    /// Used by wrapped directory's <c>CreateDirectory(DirectoryName)</c> to route the create
    /// back through the aggregate so mount resolution and read-only checks apply.
    /// </summary>
    internal IFileSystemDirectory CreateDirectoryUnderMount(AggregateFileSystemDirectory parent, DirectoryName name)
    {
        FileSystemPath childPath = JoinChild(parent.Path, name.ToString());
        return CreateDirectory(childPath);
    }

    internal IFileSystemFile CreateFileUnderMount(AggregateFileSystemDirectory parent, FileName name)
    {
        FileSystemPath childPath = JoinChild(parent.Path, name.ToString());
        return CreateFile(childPath);
    }

    /// <summary>
    /// Throws when the aggregate is disposed. Exposed to the wrappers so they share the same
    /// guard.
    /// </summary>
    private void CheckIfDisposed() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    private void CheckIfReadOnly(string operation)
    {
        if (_isReadOnly)
        {
            FileSystemException.ThrowReadOnly(operation);
        }
    }

    private static FileSystemPath Normalize(FileSystemPath path)
    {
        string text = path.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return "/";
        }
        if (text[0] != '/')
        {
            text = "/" + text;
        }
        if (text.Length > 1 && text[text.Length - 1] == '/')
        {
            text = text.Substring(0, text.Length - 1);
        }
        return FileSystemPath.Parse(text);
    }

    private static FileSystemPath JoinChild(FileSystemPath parent, string child)
    {
        string text = parent.ToString();
        if (text == "/" || string.IsNullOrEmpty(text))
        {
            return "/" + child;
        }
        return text + "/" + child;
    }

    private IFileSystemDirectory BuildRootDirectory()
    {
        // If something is mounted exactly at "/", we expose that mount's root directly. Otherwise
        // the root is synthetic and child enumeration walks the mount table.
        foreach (var mount in _mountsSorted)
        {
            if (mount.MountPath.ToString() == "/")
            {
                var providerRoot = mount.FileSystem.RootDirectory;
                return new AggregateFileSystemDirectory(this, mount, providerRoot, "/");
            }
        }
        return new AggregateSyntheticDirectory(this, "/");
    }

    private static void StreamContents(IFileSystemFile source, IFileSystemFile destination)
    {
        using var input = source.Open(FileMode.Open, FileAccess.Read);
        using var output = destination.Open(FileMode.Open, FileAccess.Write);
        input.CopyTo(output);
    }
}
