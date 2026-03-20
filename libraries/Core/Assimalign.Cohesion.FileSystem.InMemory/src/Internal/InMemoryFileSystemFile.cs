using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;

namespace Assimalign.Cohesion.FileSystem.Internal;

[DebuggerDisplay("[F] - {Path}")]
internal class InMemoryFileSystemFile : InMemoryFileSystemInfo, IFileSystemFile
{
    private readonly Lock _openLock;
    private readonly List<OpenRegistration> _openRegistrations;

    private FileName _name;
    private InMemoryFileSystemDirectory _directory;
    private bool _isDiposed;
    private bool _isDeleted;
    private bool _storageReleased;

    public InMemoryFileSystemFile(FileName name, InMemoryFileSystemDirectory directory, InMemoryFileSystem fileSystem)
        : base(fileSystem, directory.CultureInfo, directory.IgnoreCase)
    {
        _openLock = new Lock();
        _openRegistrations = new List<OpenRegistration>();
        _name = name;
        _directory = directory;
        Content = new InMemoryFileContent(this);
    }

    public Size Size => Content.Length;

    public FileName Name => _name;

    public InMemoryFileContent Content { get; private set; }

    public InMemoryFileSystemDirectory Directory => _directory;

    IFileSystemDirectory IFileSystemFile.Directory => Directory;

    public Stream Open()
    {
        return Open(FileMode.Open);
    }

    public Stream Open(FileMode fileMode)
    {
        return Open(fileMode, FileSystem.IsReadOnly ? FileAccess.Read : FileAccess.ReadWrite);
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess)
    {
        return Open(fileMode, fileAccess, FileShare.None);
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        ValidateOpenArguments(fileMode, fileAccess);

        if (FileSystem.IsReadOnly && (fileMode != FileMode.Open || fileAccess != FileAccess.Read))
        {
            throw new InvalidOperationException("The file system is read-only. Only FileMode.Open with FileAccess.Read is allowed.");
        }

        var isReading = (fileAccess & FileAccess.Read) != 0;
        var isWriting = (fileAccess & FileAccess.Write) != 0;
        var appendOnly = fileMode == FileMode.Append;
        var registration = RegisterOpen(fileAccess, fileShare);

        try
        {
            var stream = new InMemoryFileStream(
                this,
                isReading,
                isWriting,
                appendOnly,
                () => ReleaseOpen(registration));

            if (fileMode is FileMode.Create or FileMode.Truncate)
            {
                stream.SetLength(0);
            }

            return stream;
        }
        catch
        {
            ReleaseOpen(registration);
            throw;
        }
    }

    internal void EnsureDeleteAllowed(FileSystemPath path)
    {
        lock (_openLock)
        {
            EnsureDeleteAllowedUnsafe(path);
        }
    }

    internal void BeginDelete(FileSystemPath path)
    {
        lock (_openLock)
        {
            EnsureDeleteAllowedUnsafe(path);
            _isDeleted = true;
            SetUpdatedOn(DateTime.Now);
        }
    }

    internal void MarkDeleted()
    {
        lock (_openLock)
        {
            _isDeleted = true;
            SetUpdatedOn(DateTime.Now);
            ReleaseStorageIfEligible();
        }
    }

    internal void MoveTo(FileName name, InMemoryFileSystemDirectory directory)
    {
        lock (_openLock)
        {
            if (_isDeleted)
            {
                throw new FileNotFoundException("The file no longer exists.", Path);
            }

            _name = name;
            _directory = directory;
            SetUpdatedOn(DateTime.Now);
        }
    }

    public IFileSystemEventToken Watch()
    {
        return new InMemoryFileSystemEventToken(this, Glob.Parse(Path));
    }

    public override void Dispose()
    {
        ObjectDisposedException.ThrowIf(_isDiposed, this);

        Lock(LockPolicy.Exclusive);

        try
        {
            if (_directory.IsLocked)
            {
                // TODO: Need to go through code path to see if child ever needs to lock parent
            }

            _directory.Entries.Remove(Path);
            _isDeleted = true;
            _isDiposed = true;
        }
        finally
        {
            Unlock();

            base.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    private OpenRegistration RegisterOpen(FileAccess access, FileShare share)
    {
        lock (_openLock)
        {
            if (_isDeleted)
            {
                throw new FileNotFoundException("The file no longer exists.", Path);
            }

            foreach (var registration in _openRegistrations)
            {
                if (!CanShare(registration.Share, access) || !CanShare(share, registration.Access))
                {
                    throw CreateSharingViolation(Path);
                }
            }

            var openRegistration = new OpenRegistration(access, share);
            _openRegistrations.Add(openRegistration);
            return openRegistration;
        }
    }

    private void ReleaseOpen(OpenRegistration registration)
    {
        lock (_openLock)
        {
            _openRegistrations.Remove(registration);
            ReleaseStorageIfEligible();
        }
    }

    private void ReleaseStorageIfEligible()
    {
        if (!_isDeleted || _storageReleased || _openRegistrations.Count != 0)
        {
            return;
        }

        _storageReleased = true;

        if (Content.Length > 0)
        {
            FileSystem.IncrementSpaceUsed(-Content.Length);
        }
    }

    private static bool CanShare(FileShare share, FileAccess access)
    {
        if ((access & FileAccess.Read) != 0 && !share.HasFlag(FileShare.Read))
        {
            return false;
        }

        if ((access & FileAccess.Write) != 0 && !share.HasFlag(FileShare.Write))
        {
            return false;
        }

        return true;
    }

    private void EnsureDeleteAllowedUnsafe(FileSystemPath path)
    {
        if (_isDeleted)
        {
            FileSystemException.ThrowFileNotFound(path);
        }

        foreach (var registration in _openRegistrations)
        {
            if (!registration.Share.HasFlag(FileShare.Delete))
            {
                FileSystemException.ThrowPathInUse(path, CreateSharingViolation(path));
            }
        }
    }

    private static IOException CreateSharingViolation(FileSystemPath path)
    {
        return new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
    }

    private static void ValidateOpenArguments(FileMode fileMode, FileAccess fileAccess)
    {
        if (fileMode == FileMode.Append && (fileAccess & FileAccess.Read) != 0)
        {
            throw new ArgumentException("Combining FileMode.Append with FileAccess.Read is invalid.", nameof(fileAccess));
        }

        if (fileMode == FileMode.Append && (fileAccess & FileAccess.Write) == 0)
        {
            throw new ArgumentException("FileMode.Append requires FileAccess.Write.", nameof(fileAccess));
        }

        if (fileMode == FileMode.Truncate && (fileAccess & FileAccess.Write) == 0)
        {
            throw new ArgumentException("FileMode.Truncate requires FileAccess.Write.", nameof(fileAccess));
        }

        if (fileMode == FileMode.CreateNew)
        {
            throw new IOException("The file already exists.");
        }
    }

    private sealed class OpenRegistration
    {
        public OpenRegistration(FileAccess access, FileShare share)
        {
            Access = access;
            Share = share;
        }

        public FileAccess Access { get; }

        public FileShare Share { get; }
    }
}
