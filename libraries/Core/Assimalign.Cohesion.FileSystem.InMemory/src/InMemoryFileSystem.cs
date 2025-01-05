using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.FileSystem.Internal;

public class InMemoryFileSystem : IFileSystem
{
    // The locking strategy is based on https://www.kernel.org/doc/Documentation/filesystems/directory-locking

   // private readonly object _dispatcherLock;
    //private FileSystemEventDispatcher<Watcher>? _dispatcher;

    private readonly FsNodeLock nodeLock;
    private readonly InMemoryFileSystemDirectory rootDirectory;

    public InMemoryFileSystem(string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            ThrowHelper.ThrowArgumentNullException(nameof(root));
        }

        rootDirectory = new InMemoryFileSystemDirectory(new FsDirectoryNode(this));
        nodeLock = new FsNodeLock();
        //_dispatcherLock = new object();
    }

    public InMemoryFileSystem() : this("/")
    {
    }

    
    //protected InMemoryFileSystem(InMemoryFileSystem copyFrom)
    //{
    //    if (copyFrom is null) throw new ArgumentNullException(nameof(copyFrom));
    //    Debug.Assert(copyFrom.nodeLock.IsLocked);
    //    _rootDirectory = (FsDirectoryNode)copyFrom._rootDirectory.Clone(null, null);
    //    nodeLock = new FsNodeReadWriteLock();
    //    _dispatcherLock = new object();
    //}


    public string Name => throw new NotImplementedException();
    public Size Size => throw new NotImplementedException();
    public Size Space => throw new NotImplementedException();
    public Size SpaceUsed => throw new NotImplementedException();
    public IFileSystemDirectory RootDirectory => rootDirectory;

    public void CopyFile(Path source, Path destination)
    {
        throw new NotImplementedException();
    }

    public IFileSystemDirectory CreateDirectory(Path path)
    {
        EnterFileSystemShared();
        try
        {
            CreateFsDirectoryNode(path);
           // TryGetDispatcher()?.RaiseCreated(path);
        }
        finally
        {
            ExitFileSystemShared();
        }
    }

    public IFileSystemFile CreateFile(Path path)
    {
        throw new NotImplementedException();
    }

    public void DeleteDirectory(Path path)
    {
        throw new NotImplementedException();
    }

    public void DeleteFile(Path path)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public bool Exist(Path path)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        throw new NotImplementedException();
    }

    public IFileSystemDirectory GetDirectory(Path path)
    {
        throw new NotImplementedException();
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    public IFileSystemFile GetFile(Path path)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IFileSystemFile> GetFiles()
    {
        throw new NotImplementedException();
    }

    public void Move(Path source, Path destination)
    {
        throw new NotImplementedException();
    }

    public IFileSystemChangeToken Watch(string filter)
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException();
    }





    public InMemoryFileSystem Clone()
    {
        EnterFileSystemExclusive();
        try
        {
            return CloneImpl();
        }
        finally
        {
            ExitFileSystemExclusive();
        }
    }
    protected virtual InMemoryFileSystem CloneImpl()
    {
        return new InMemoryFileSystem(this);
    }
    protected override string DebuggerDisplay()
    {
        return $"{base.DebuggerDisplay()} {_rootDirectory.DebuggerDisplay()}";
    }
    // ----------------------------------------------
    // Directory API
    // ----------------------------------------------
    protected void CreateDirectoryImpl(Path path)
    {
        
    }
    protected override bool DirectoryExistsImpl(Path path)
    {
        if (path == Path.Root)
        {
            return true;
        }
        EnterFileSystemShared();
        try
        {
            // NodeCheck doesn't take a lock, on the return node
            // but allows us to check if it is a directory or a file
            var result = EnterFindNode(path, FindNodeFlags.NodeCheck);
            try
            {
                return result.Node is FsDirectoryNode;
            }
            finally
            {
                ExitFindNode(result);
            }
        }
        finally
        {
            ExitFileSystemShared();
        }
    }
    protected override void MoveDirectoryImpl(Path srcPath, Path destPath)
    {
        MoveFileOrDirectory(srcPath, destPath, true);
    }
    protected override void DeleteDirectoryImpl(Path path, bool isRecursive)
    {
        EnterFileSystemShared();
        try
        {
            var result = EnterFindNode(path, FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.NodeExclusive);
            bool deleteRootDirectory = false;
            try
            {
                ValidateDirectory(result.Node, path);
                if (result.Node.IsReadOnly)
                {
                    throw new IOException($"Access to the path `{path}` is denied");
                }
                using (var locks = new FsNodeList(this))
                {
                    TryLockExclusive(result.Node, locks, isRecursive, path);
                    // Check that files are not readonly
                    foreach (var lockFile in locks)
                    {
                        var node = lockFile.Value;
                        if (node.IsReadOnly)
                        {
                            throw new UnauthorizedAccessException($"Access to path `{path}` is denied.");
                        }
                    }
                    // We remove all elements
                    for (var i = locks.Count - 1; i >= 0; i--)
                    {
                        var lockFile = locks[i];
                        locks.RemoveAt(i);
                        lockFile.Value.DetachFromParent();
                        lockFile.Value.Dispose();
                        ExitExclusive(lockFile.Value);
                    }
                }
                deleteRootDirectory = true;
            }
            finally
            {
                if (deleteRootDirectory && result.Node != null)
                {
                    result.Node.DetachFromParent();
                    result.Node.Dispose();
                    TryGetDispatcher()?.RaiseDeleted(path);
                }
                ExitFindNode(result);
            }
        }
        finally
        {
            ExitFileSystemShared();
        }
    }
    // ----------------------------------------------
    // File API
    // ----------------------------------------------
    protected override void CopyFileImpl(Path srcPath, Path destPath, bool overwrite)
    {
        EnterFileSystemShared();
        try
        {
            var srcResult = EnterFindNode(srcPath, FindNodeFlags.NodeShared);
            try
            {
                // The source file must exist
                var srcNode = srcResult.Node;
                if (srcNode is FsDirectoryNode)
                {
                    throw new UnauthorizedAccessException($"Cannot copy file. The path `{srcPath}` is a directory");
                }
                if (srcNode is null)
                {
                    throw NewFileNotFoundException(srcPath);
                }
                var destResult = EnterFindNode(destPath, FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.NodeExclusive);
                var destFileName = destResult.Name;
                var destDirectory = destResult.Directory;
                var destNode = destResult.Node;
                try
                {
                    // The dest file may exist
                    if (destDirectory is null)
                    {
                        throw NewDirectoryNotFoundException(destPath);
                    }
                    if (destNode is FsDirectoryNode)
                    {
                        throw new IOException($"The target file `{destPath}` is a directory, not a file.");
                    }
                    // If the destination is empty, we need to create it
                    if (destNode is null)
                    {
                        // Constructor copies and attaches to directory for us
                        var newFsFileNode = new FsFileNode(this, destDirectory, destFileName, (FsFileNode)srcNode);
                        TryGetDispatcher()?.RaiseCreated(destPath);
                        TryGetDispatcher()?.RaiseChange(destPath);
                    }
                    else if (overwrite)
                    {
                        if (destNode.IsReadOnly)
                        {
                            throw new UnauthorizedAccessException($"Access to path `{destPath}` is denied.");
                        }
                        var destFsFileNode = (FsFileNode)destNode;
                        destFsFileNode.Content.CopyFrom(((FsFileNode)srcNode).Content);
                        TryGetDispatcher()?.RaiseChange(destPath);
                    }
                    else
                    {
                        throw new IOException($"The destination file path `{destPath}` already exist and overwrite is false");
                    }
                }
                finally
                {
                    if (destNode != null)
                    {
                        ExitExclusive(destNode);
                    }
                    if (destDirectory != null)
                    {
                        ExitExclusive(destDirectory);
                    }
                }
            }
            finally
            {
                ExitFindNode(srcResult);
            }
        }
        finally
        {
            ExitFileSystemShared();
        }
    }
    protected override void ReplaceFileImpl(Path srcPath, Path destPath, Path destBackupPath, bool ignoreMetadataErrors)
    {
        // Get the directories of src/dest/backup
        var parentSrcPath = srcPath.GetDirectory();
        var parentDestPath = destPath.GetDirectory();
        var parentDestBackupPath = destBackupPath.IsNull ? new Path() : destBackupPath.GetDirectory();
        // Simple case: src/dest/backup in the same folder
        var isSameFolder = parentSrcPath == parentDestPath && (destBackupPath.IsNull || (parentDestBackupPath == parentSrcPath));
        // Else at least one folder is different. This is a rename semantic (as per the locking guidelines)
        var paths = new List<KeyValuePair<Path, int>>
        {
                new KeyValuePair<Path, int>(srcPath, 0),
                new KeyValuePair<Path, int>(destPath, 1)
            };
        if (!destBackupPath.IsNull)
        {
            paths.Add(new KeyValuePair<Path, int>(destBackupPath, 2));
        }
        paths.Sort((p1, p2) => string.Compare(p1.Key.FullName, p2.Key.FullName, StringComparison.Ordinal));
        // We need to take the lock on the folders in the correct order to avoid deadlocks
        // So we sort the srcPath and destPath in alphabetical order
        // (if srcPath is a subFolder of destPath, we will lock first destPath parent Folder, and then srcFolder)
        if (isSameFolder)
        {
            EnterFileSystemShared();
        }
        else
        {
            EnterFileSystemExclusive();
        }
        try
        {
            var results = new NodeResult[destBackupPath.IsNull ? 2 : 3];
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    var pathPair = paths[i];
                    var flags = FindNodeFlags.KeepParentNodeExclusive;
                    if (pathPair.Value != 2)
                    {
                        flags |= FindNodeFlags.NodeExclusive;
                    }
                    else
                    {
                        flags |= FindNodeFlags.NodeShared;
                    }
                    results[pathPair.Value] = EnterFindNode(pathPair.Key, flags, results);
                }
                var srcResult = results[0];
                var destResult = results[1];
                ValidateFile(srcResult.Node, srcPath);
                ValidateFile(destResult.Node, destPath);
                if (!destBackupPath.IsNull)
                {
                    var backupResult = results[2];
                    ValidateDirectory(backupResult.Directory, destPath);
                    if (backupResult.Node != null)
                    {
                        ValidateFile(backupResult.Node, destBackupPath);
                        backupResult.Node.DetachFromParent();
                        backupResult.Node.Dispose();
                        TryGetDispatcher()?.RaiseDeleted(destBackupPath);
                    }
                    destResult.Node.DetachFromParent();
                    destResult.Node.AttachToParent(backupResult.Directory!, backupResult.Name!);
                    TryGetDispatcher()?.RaiseRenamed(destBackupPath, destPath);
                }
                else
                {
                    destResult.Node.DetachFromParent();
                    destResult.Node.Dispose();
                    TryGetDispatcher()?.RaiseDeleted(destPath);
                }
                srcResult.Node.DetachFromParent();
                srcResult.Node.AttachToParent(destResult.Directory!, destResult.Name!);
                TryGetDispatcher()?.RaiseRenamed(destPath, srcPath);
            }
            finally
            {
                for (int i = results.Length - 1; i >= 0; i--)
                {
                    ExitFindNode(results[i]);
                }
            }
        }
        finally
        {
            if (isSameFolder)
            {
                ExitFileSystemShared();
            }
            else
            {
                ExitFileSystemExclusive();
            }
        }
    }
    protected long GetFileLengthImpl(Path path)
    {
        EnterFileSystemShared();
        try
        {
            return ((FsFileNode)FindNodeSafe(path, true)).Content.Length;
        }
        finally
        {
            ExitFileSystemShared();
        }
    }
    protected bool FileExistsImpl(Path path)
    {
        EnterFileSystemShared();
        try
        {
            // NodeCheck doesn't take a lock, on the return node
            // but allows us to check if it is a directory or a file
            var result = EnterFindNode(path, FindNodeFlags.NodeCheck);
            ExitFindNode(result);
            return result.Node is FsFileNode;
        }
        finally
        {
            ExitFileSystemShared();
        }
    }
    protected override void MoveFileImpl(Path srcPath, Path destPath)
    {
        MoveFileOrDirectory(srcPath, destPath, false);
    }
    protected override void DeleteFileImpl(Path path)
    {
        EnterFileSystemShared();
        try
        {
            var result = EnterFindNode(path, FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.NodeExclusive);
            try
            {
                var srcNode = result.Node;
                if (srcNode is null)
                {
                    // If the file to be deleted does not exist, no exception is thrown.
                    return;
                }
                if (srcNode is FsDirectoryNode || srcNode.IsReadOnly)
                {
                    throw new UnauthorizedAccessException($"Access to path `{path}` is denied.");
                }
                srcNode.DetachFromParent();
                srcNode.Dispose();
                TryGetDispatcher()?.RaiseDeleted(path);
            }
            finally
            {
                ExitFindNode(result);
            }
        }
        finally
        {
            ExitFileSystemShared();
        }
    }
    protected override Stream OpenFileImpl(Path path, FileMode mode, FileAccess access, FileShare share)
    {
        if (mode == FileMode.Append && (access & FileAccess.Read) != 0)
        {
            throw new ArgumentException("Combining FileMode: Append with FileAccess: Read is invalid.", nameof(access));
        }
        var isReading = (access & FileAccess.Read) != 0;
        var isWriting = (access & FileAccess.Write) != 0;
        var isExclusive = share == FileShare.None;
        EnterFileSystemShared();
        FsDirectoryNode? parentDirectory = null;
        FsFileNode? fileNodeToRelease = null;
        try
        {
            var result = EnterFindNode(path, (isExclusive ? FindNodeFlags.NodeExclusive : FindNodeFlags.NodeShared) | FindNodeFlags.KeepParentNodeExclusive, share);
            if (result.Directory is null)
            {
                ExitFindNode(result);
                throw NewDirectoryNotFoundException(path);
            }
            if (result.Node is FsDirectoryNode || (isWriting && result.Node != null && result.Node.IsReadOnly))
            {
                ExitFindNode(result);
                throw new UnauthorizedAccessException($"Access to the path `{path}` is denied.");
            }
            var filename = result.Name;
            parentDirectory = result.Directory;
            var srcNode = result.Node;
            var fileNode = (FsFileNode)srcNode!;
            // Append: Opens the file if it exists and seeks to the end of the file, or creates a new file. 
            //         This requires FileIOPermissionAccess.Append permission. FileMode.Append can be used only in 
            //         conjunction with FileAccess.Write. Trying to seek to a position before the end of the file 
            //         throws an IOException exception, and any attempt to read fails and throws a 
            //         NotSupportedException exception.
            //
            //
            // CreateNew: Specifies that the operating system should create a new file.This requires 
            //            FileIOPermissionAccess.Write permission. If the file already exists, an IOException 
            //            exception is thrown.
            //
            // Open: Specifies that the operating system should open an existing file. The ability to open 
            //       the file is dependent on the value specified by the FileAccess enumeration. 
            //       A System.IO.FileNotFoundException exception is thrown if the file does not exist.
            //
            // OpenOrCreate: Specifies that the operating system should open a file if it exists; 
            //               otherwise, a new file should be created. If the file is opened with 
            //               FileAccess.Read, FileIOPermissionAccess.Read permission is required. 
            //               If the file access is FileAccess.Write, FileIOPermissionAccess.Write permission 
            //               is required. If the file is opened with FileAccess.ReadWrite, both 
            //               FileIOPermissionAccess.Read and FileIOPermissionAccess.Write permissions 
            //               are required. 
            //
            // Truncate: Specifies that the operating system should open an existing file. 
            //           When the file is opened, it should be truncated so that its size is zero bytes. 
            //           This requires FileIOPermissionAccess.Write permission. Attempts to read from a file 
            //           opened with FileMode.Truncate cause an ArgumentException exception.
            // Create: Specifies that the operating system should create a new file.If the file already exists, 
            //         it will be overwritten.This requires FileIOPermissionAccess.Write permission. 
            //         FileMode.Create is equivalent to requesting that if the file does not exist, use CreateNew; 
            //         otherwise, use Truncate. If the file already exists but is a hidden file, 
            //         an UnauthorizedAccessException exception is thrown.
            bool shouldTruncate = false;
            bool shouldAppend = false;
            if (mode == FileMode.Create)
            {
                if (fileNode != null)
                {
                    mode = FileMode.Open;
                    shouldTruncate = true;
                }
                else
                {
                    mode = FileMode.CreateNew;
                }
            }
            if (mode == FileMode.OpenOrCreate)
            {
                mode = fileNode != null ? FileMode.Open : FileMode.CreateNew;
            }
            if (mode == FileMode.Append)
            {
                if (fileNode != null)
                {
                    mode = FileMode.Open;
                    shouldAppend = true;
                }
                else
                {
                    mode = FileMode.CreateNew;
                }
            }
            if (mode == FileMode.Truncate)
            {
                if (fileNode != null)
                {
                    mode = FileMode.Open;
                    shouldTruncate = true;
                }
                else
                {
                    throw NewFileNotFoundException(path);
                }
            }
            // Here we should only have Open or CreateNew
            Debug.Assert(mode == FileMode.Open || mode == FileMode.CreateNew);
            if (mode == FileMode.CreateNew)
            {
                // This is not completely accurate to throw an exception (as we have been called with an option to OpenOrCreate)
                // But we assume that between the beginning of the method and here, the filesystem is not changing, and 
                // if it is, it is an unfortunate conrurrency
                if (fileNode != null)
                {
                    fileNodeToRelease = fileNode;
                    throw NewDestinationFileExistException(path);
                }
                fileNode = new FsFileNode(this, parentDirectory, filename, null);
                TryGetDispatcher()?.RaiseCreated(path);
                if (isExclusive)
                {
                    EnterExclusive(fileNode, path);
                }
                else
                {
                    EnterShared(fileNode, path, share);
                }
            }
            else
            {
                if (fileNode is null)
                {
                    throw NewFileNotFoundException(path);
                }
                ExitExclusive(parentDirectory);
                parentDirectory = null;
            }
            // TODO: Add checks between mode and access
            // Create a memory file stream
            var stream = new MemoryFileStream(this, fileNode, isReading, isWriting, isExclusive);
            if (shouldAppend)
            {
                stream.Position = stream.Length;
            }
            else if (shouldTruncate)
            {
                stream.SetLength(0);
            }
            return stream;
        }
        finally
        {
            if (fileNodeToRelease != null)
            {
                if (isExclusive)
                {
                    ExitExclusive(fileNodeToRelease);
                }
                else
                {
                    ExitShared(fileNodeToRelease);
                }
            }
            if (parentDirectory != null)
            {
                ExitExclusive(parentDirectory);
            }
            ExitFileSystemShared();
        }
    }
    // ----------------------------------------------
    // Metadata API
    // ----------------------------------------------
    protected override FileAttributes GetAttributesImpl(Path path)
    {
        var node = FindNodeSafe(path, false);
        var attributes = node.Attributes;
        if (node is FsDirectoryNode)
        {
            attributes |= FileAttributes.Directory;
        }
        else if (attributes == 0)
        {
            // If this is a file and there is no attributes, return Normal
            attributes = FileAttributes.Normal;
        }
        return attributes;
    }
    protected override void SetAttributesImpl(Path path, FileAttributes attributes)
    {
        // We don't store the attributes Normal or directory
        // As they are returned by GetAttributes and we don't want
        // to duplicate the information with the type inheritance (FsFileNode or FsDirectoryNode)
        attributes &= ~FileAttributes.Normal;
        attributes &= ~FileAttributes.Directory;
        var node = FindNodeSafe(path, false);
        node.Attributes = attributes;
        TryGetDispatcher()?.RaiseChange(path);
    }
    protected override DateTime GetCreationTimeImpl(Path path)
    {
        return TryFindNodeSafe(path)?.CreationTime ?? DefaultFileTime;
    }
    protected override void SetCreationTimeImpl(Path path, DateTime time)
    {
        FindNodeSafe(path, false).CreationTime = time;
        TryGetDispatcher()?.RaiseChange(path);
    }
    protected override DateTime GetLastAccessTimeImpl(Path path)
    {
        return TryFindNodeSafe(path)?.LastAccessTime ?? DefaultFileTime;
    }
    protected override void SetLastAccessTimeImpl(Path path, DateTime time)
    {
        FindNodeSafe(path, false).LastAccessTime = time;
        TryGetDispatcher()?.RaiseChange(path);
    }
    protected override DateTime GetLastWriteTimeImpl(Path path)
    {
        return TryFindNodeSafe(path)?.LastWriteTime ?? DefaultFileTime;
    }
    protected override void SetLastWriteTimeImpl(Path path, DateTime time)
    {
        FindNodeSafe(path, false).LastWriteTime = time;
        TryGetDispatcher()?.RaiseChange(path);
    }
    protected override void CreateSymbolicLinkImpl(Path path, Path pathToTarget)
    {
        throw new NotSupportedException("Symbolic links are not supported by InMemoryFileSystem");
    }
    protected override bool TryResolveLinkTargetImpl(Path linkPath, out Path resolvedPath)
    {
        resolvedPath = default;
        return false;
    }
    // ----------------------------------------------
    // Search API
    // ----------------------------------------------
    protected override IEnumerable<Path> EnumeratePathsImpl(Path path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        var search = SearchPattern.Parse(ref path, ref searchPattern);
        var foldersToProcess = new List<Path>();
        foldersToProcess.Add(path);
        var entries = new SortedSet<Path>(Path.DefaultComparerIgnoreCase);
        while (foldersToProcess.Count > 0)
        {
            var directoryPath = foldersToProcess[0];
            foldersToProcess.RemoveAt(0);
            int dirIndex = 0;
            entries.Clear();
            // This is important that here we don't lock the FileSystemShared
            // or the visited folder while returning a yield otherwise the finally
            // may never be executed if the caller of this method decide to not
            // Dispose the IEnumerable (because the generated IEnumerable
            // doesn't have a finalizer calling Dispose)
            // This is why the yield is performed outside this block
            EnterFileSystemShared();
            try
            {
                var result = EnterFindNode(directoryPath, FindNodeFlags.NodeShared);
                try
                {
                    if (directoryPath == path)
                    {
                        // The first folder must be a directory, if it is not, throw an error
                        ValidateDirectory(result.Node, directoryPath);
                    }
                    else
                    {
                        // Might happen during the time a FsDirectoryNode is enqueued into foldersToProcess
                        // and the time we are going to actually visit it, it might have been
                        // removed in the meantime, so we make sure here that we have a folder
                        // and we don't throw an error if it is not
                        if (result.Node is not FsDirectoryNode)
                        {
                            continue;
                        }
                    }
                    var directory = (FsDirectoryNode)result.Node;
                    foreach (var nodePair in directory.Children)
                    {
                        if (nodePair.Value is FsFileNode && searchTarget == SearchTarget.Directory)
                        {
                            continue;
                        }
                        var isEntryMatching = search.Match(nodePair.Key);
                        var canFollowFolder = searchOption == SearchOption.AllDirectories && nodePair.Value is FsDirectoryNode;
                        var addEntry = (nodePair.Value is FsFileNode && searchTarget != SearchTarget.Directory && isEntryMatching)
                                       || (nodePair.Value is FsDirectoryNode && searchTarget != SearchTarget.File && isEntryMatching);
                        var fullPath = directoryPath / nodePair.Key;
                        if (canFollowFolder)
                        {
                            foldersToProcess.Insert(dirIndex++, fullPath);
                        }
                        if (addEntry)
                        {
                            entries.Add(fullPath);
                        }
                    }
                }
                finally
                {
                    ExitFindNode(result);
                }
            }
            finally
            {
                ExitFileSystemShared();
            }
            // We return all the elements of visited directory in one shot, outside the previous lock block
            foreach (var entry in entries)
            {
                yield return entry;
            }
        }
    }
    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(Path path, SearchOption searchOption, SearchPredicate? searchPredicate)
    {
        var foldersToProcess = new List<Path>();
        foldersToProcess.Add(path);
        var entries = new List<FileSystemItem>();
        while (foldersToProcess.Count > 0)
        {
            var directoryPath = foldersToProcess[0];
            foldersToProcess.RemoveAt(0);
            int dirIndex = 0;
            entries.Clear();
            // This is important that here we don't lock the FileSystemShared
            // or the visited folder while returning a yield otherwise the finally
            // may never be executed if the caller of this method decide to not
            // Dispose the IEnumerable (because the generated IEnumerable
            // doesn't have a finalizer calling Dispose)
            // This is why the yield is performed outside this block
            EnterFileSystemShared();
            try
            {
                var result = EnterFindNode(directoryPath, FindNodeFlags.NodeShared);
                try
                {
                    if (directoryPath == path)
                    {
                        // The first folder must be a directory, if it is not, throw an error
                        ValidateDirectory(result.Node, directoryPath);
                    }
                    else
                    {
                        // Might happen during the time a FsDirectoryNode is enqueued into foldersToProcess
                        // and the time we are going to actually visit it, it might have been
                        // removed in the meantime, so we make sure here that we have a folder
                        // and we don't throw an error if it is not
                        if (result.Node is not FsDirectoryNode)
                        {
                            continue;
                        }
                    }
                    var directory = (FsDirectoryNode)result.Node;
                    foreach (var nodePair in directory.Children)
                    {
                        var node = nodePair.Value;
                        var canFollowFolder = searchOption == SearchOption.AllDirectories && nodePair.Value is FsDirectoryNode;
                        var fullPath = directoryPath / nodePair.Key;
                        if (canFollowFolder)
                        {
                            foldersToProcess.Insert(dirIndex++, fullPath);
                        }
                        var item = new FileSystemItem
                        {
                            FileSystem = this,
                            AbsolutePath = fullPath,
                            Path = fullPath,
                            Attributes = node.Attributes,
                            CreationTime = node.CreationTime,
                            LastWriteTime = node.LastWriteTime,
                            LastAccessTime = node.LastAccessTime,
                            Length = node is FsFileNode fileNode ? fileNode.Content.Length : 0,
                        };
                        if (searchPredicate == null || searchPredicate(ref item))
                        {
                            entries.Add(item);
                        }
                    }
                }
                finally
                {
                    ExitFindNode(result);
                }
            }
            finally
            {
                ExitFileSystemShared();
            }
            // We return all the elements of visited directory in one shot, outside the previous lock block
            foreach (var entry in entries)
            {
                yield return entry;
            }
        }
    }
    // ----------------------------------------------
    // Watch API
    // ----------------------------------------------
    protected override IFileSystemWatcher WatchImpl(Path path)
    {
        var watcher = new Watcher(this, path);
        GetOrCreateDispatcher().Add(watcher);
        return watcher;
    }
    private class Watcher : FileSystemWatcher
    {
        private readonly InMemoryFileSystem _fileSystem;
        public Watcher(InMemoryFileSystem fileSystem, Path path)
            : base(fileSystem, path)
        {
            _fileSystem = fileSystem;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_fileSystem.IsDisposing)
            {
                _fileSystem.TryGetDispatcher()?.Remove(this);
            }
        }
    }
    // ----------------------------------------------
    // Path API
    // ----------------------------------------------
    protected override string ConvertPathToInternalImpl(Path path)
    {
        return path.FullName;
    }
    protected override Path ConvertPathFromInternalImpl(string innerPath)
    {
        return new Path(innerPath);
    }
    // ----------------------------------------------
    // Internals
    // ----------------------------------------------
    private void MoveFileOrDirectory(Path srcPath, Path destPath, bool expectDirectory)
    {
        var parentSrcPath = srcPath.GetDirectory();
        var parentDestPath = destPath.GetDirectory();
        void AssertNoDestination(FsNode? node)
        {
            if (expectDirectory)
            {
                if (node is FsFileNode || node != null)
                {
                    throw NewDestinationFileExistException(destPath);
                }
            }
            else
            {
                if (node is FsDirectoryNode || node != null)
                {
                    throw NewDestinationDirectoryExistException(destPath);
                }
            }
        }
        // Same directory move
        bool isSamefolder = parentSrcPath == parentDestPath;
        // Check that Destination folder is not a subfolder of source directory
        if (!isSamefolder && expectDirectory)
        {
            var checkParentDestDirectory = destPath.GetDirectory();
            while (!checkParentDestDirectory.IsNull)
            {
                if (checkParentDestDirectory == srcPath)
                {
                    throw new IOException($"Cannot move the source directory `{srcPath}` to a a sub-folder of itself `{destPath}`");
                }
                checkParentDestDirectory = checkParentDestDirectory.GetDirectory();
            }
        }
        // We need to take the lock on the folders in the correct order to avoid deadlocks
        // So we sort the srcPath and destPath in alphabetical order
        // (if srcPath is a subFolder of destPath, we will lock first destPath parent Folder, and then srcFolder)
        bool isLockInverted = !isSamefolder && string.Compare(srcPath.FullName, destPath.FullName, StringComparison.Ordinal) > 0;
        if (isSamefolder)
        {
            EnterFileSystemShared();
        }
        else
        {
            EnterFileSystemExclusive();
        }
        try
        {
            var srcResult = new NodeResult();
            var destResult = new NodeResult();
            try
            {
                if (isLockInverted)
                {
                    destResult = EnterFindNode(destPath, FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.NodeShared);
                    srcResult = EnterFindNode(srcPath, FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.NodeExclusive, destResult);
                }
                else
                {
                    srcResult = EnterFindNode(srcPath, FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.NodeExclusive);
                    destResult = EnterFindNode(destPath, FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.NodeShared, srcResult);
                }
                if (expectDirectory)
                {
                    ValidateDirectory(srcResult.Node, srcPath);
                }
                else
                {
                    ValidateFile(srcResult.Node, srcPath);
                }
                ValidateDirectory(destResult.Directory, destPath);
                AssertNoDestination(destResult.Node);
                srcResult.Node.DetachFromParent();
                srcResult.Node.AttachToParent(destResult.Directory, destResult.Name!);
                TryGetDispatcher()?.RaiseDeleted(srcPath);
                TryGetDispatcher()?.RaiseCreated(destPath);
            }
            finally
            {
                if (isLockInverted)
                {
                    ExitFindNode(srcResult);
                    ExitFindNode(destResult);
                }
                else
                {
                    ExitFindNode(destResult);
                    ExitFindNode(srcResult);
                }
            }
        }
        finally
        {
            if (isSamefolder)
            {
                ExitFileSystemShared();
            }
            else
            {
                ExitFileSystemExclusive();
            }
        }
    }
    private static void ValidateDirectory([NotNull] FsNode? node, Path srcPath)
    {
        if (node is FsFileNode)
        {
            throw new IOException($"The source directory `{srcPath}` is a file");
        }
        if (node is null)
        {
            throw NewDirectoryNotFoundException(srcPath);
        }
    }
    private static void ValidateFile([NotNull] FsNode? node, Path srcPath)
    {
        if (node is null)
        {
            throw NewFileNotFoundException(srcPath);
        }
    }
    private FsNode? TryFindNodeSafe(Path path)
    {
        EnterFileSystemShared();
        try
        {
            var result = EnterFindNode(path, FindNodeFlags.NodeShared);
            try
            {
                var node = result.Node;
                return node;
            }
            finally
            {
                ExitFindNode(result);
            }
        }
        finally
        {
            ExitFileSystemShared();
        }
    }
    private FsNode FindNodeSafe(Path path, bool expectFileOnly)
    {
        var node = TryFindNodeSafe(path);
        if (node is null)
        {
            if (expectFileOnly)
            {
                throw NewFileNotFoundException(path);
            }
            throw new IOException($"The file or directory `{path}` was not found");
        }
        if (node is FsDirectoryNode)
        {
            if (expectFileOnly)
            {
                throw NewFileNotFoundException(path);
            }
        }
        return node;
    }
    private void CreateFsDirectoryNode(Path path)
    {
        ExitFindNode(EnterFindNode(path, FindNodeFlags.CreatePathIfNotExist | FindNodeFlags.NodeShared));
    }
    private readonly struct NodeResult
    {
        public NodeResult(FsDirectoryNode? directory, FsNode? node, string? name, FindNodeFlags flags)
        {
            Directory = directory;
            Node = node;
            Name = name;
            Flags = flags;
        }
        public readonly FsDirectoryNode? Directory;
        public readonly FsNode? Node;
        public readonly string? Name;
        public readonly FindNodeFlags Flags;
    }
    [Flags]
    private enum FindNodeFlags
    {
        CreatePathIfNotExist = 1 << 1,
        NodeCheck = 1 << 2,
        NodeShared = 1 << 3,
        NodeExclusive = 1 << 4,
        KeepParentNodeExclusive = 1 << 5,
        KeepParentNodeShared = 1 << 6,
    }
    private void ExitFindNode(in NodeResult nodeResult)
    {
        var flags = nodeResult.Flags;
        // Unlock first the node
        if (nodeResult.Node != null)
        {
            if ((flags & FindNodeFlags.NodeExclusive) != 0)
            {
                ExitExclusive(nodeResult.Node);
            }
            else if ((flags & FindNodeFlags.NodeShared) != 0)
            {
                ExitShared(nodeResult.Node);
            }
        }
        if (nodeResult.Directory is null)
        {
            return;
        }
        // Unlock the parent directory if necessary
        if ((flags & FindNodeFlags.KeepParentNodeExclusive) != 0)
        {
            ExitExclusive(nodeResult.Directory);
        }
        else if ((flags & FindNodeFlags.KeepParentNodeShared) != 0)
        {
            ExitShared(nodeResult.Directory);
        }
    }
    private NodeResult EnterFindNode(Path path, FindNodeFlags flags, params NodeResult[] existingNodes)
    {
        return EnterFindNode(path, flags, null, existingNodes);
    }
    private NodeResult EnterFindNode(Path path, FindNodeFlags flags, FileShare? share, params NodeResult[] existingNodes)
    {
        // TODO: Split the flags between parent and node to make the code more clear
        var result = new NodeResult();
        // This method should be always called with at least one of these
        Debug.Assert((flags & (FindNodeFlags.NodeExclusive | FindNodeFlags.NodeShared | FindNodeFlags.NodeCheck)) != 0);
        var sharePath = share ?? FileShare.Read;
        bool isLockOnRootAlreadyTaken = IsNodeAlreadyLocked(rootDirectory.Node, existingNodes);
        // Even if it is not valid, the EnterFindNode may be called with a root directory
        // So we handle it as a special case here
        if (path == RootDirectory.Path)
        {
            if (!isLockOnRootAlreadyTaken)
            {
                if ((flags & FindNodeFlags.NodeExclusive) != 0)
                {
                    EnterExclusive(rootDirectory.Node, path);
                }
                else if ((flags & FindNodeFlags.NodeShared) != 0)
                {
                    EnterShared(rootDirectory.Node, path, sharePath);
                }
            }
            else
            {
                // If the lock was already taken, we make sure that NodeResult
                // will not try to release it
                flags &= ~(FindNodeFlags.NodeExclusive | FindNodeFlags.NodeShared);
            }
            result = new NodeResult(null, rootDirectory.Node, null, flags);
            return result;
        }
        var isRequiringExclusiveLockForParent = (flags & (FindNodeFlags.CreatePathIfNotExist | FindNodeFlags.KeepParentNodeExclusive)) != 0;
        var parentNode = rootDirectory.Node;
        var names = path.GetSegments().ToList();
        // Walking down the nodes in locking order:
        // /a/b/c.txt
        //
        // Lock /
        // Lock /a
        // Unlock /
        // Lock /a/b
        // Unlock /a
        // Lock /a/b/c.txt
        // Start by locking the parent directory (only if it is not already locked)
        bool isParentLockTaken = false;
        if (!isLockOnRootAlreadyTaken)
        {
            EnterExclusiveOrSharedDirectoryOrBlock(rootDirectory.Node, path, isRequiringExclusiveLockForParent);
            isParentLockTaken = true;
        }
        for (var i = 0; i < names.Count && parentNode != null; i++)
        {
            var name = names[i];
            bool isLast = i + 1 == names.Count;
            FsDirectoryNode? nextParent = null;
            bool isNextParentLockTaken = false;
            try
            {
                FsNode? subNode;
                if (!parentNode.Children.TryGetValue(name, out subNode))
                {
                    if ((flags & FindNodeFlags.CreatePathIfNotExist) != 0)
                    {
                        subNode = new FsDirectoryNode(this, parentNode, name);
                    }
                }
                else
                {
                    // If we are trying to create a directory and one of the node on the way is a file
                    // this is an error
                    if ((flags & FindNodeFlags.CreatePathIfNotExist) != 0 && subNode is FsFileNode)
                    {
                        throw new IOException($"Cannot create directory `{path}` on an existing file");
                    }
                }
                // Special case of the last entry
                if (isLast)
                {
                    // If the lock was not taken by the parent, modify the flags 
                    // so that Exit(NodeResult) will not try to release the lock on the parent
                    if (!isParentLockTaken)
                    {
                        flags &= ~(FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.KeepParentNodeShared);
                    }
                    result = new NodeResult(parentNode, subNode, name, flags);
                    // The last subnode may be null but we still want to return a valid parent
                    // otherwise, lock the final node if necessary
                    if (subNode != null)
                    {
                        if ((flags & FindNodeFlags.NodeExclusive) != 0)
                        {
                            EnterExclusive(subNode, path);
                        }
                        else if ((flags & FindNodeFlags.NodeShared) != 0)
                        {
                            EnterShared(subNode, path, sharePath);
                        }
                    }
                    // After we have taken the lock, and we need to keep a lock on the parent, make sure
                    // that the finally {} below will not unlock the parent
                    // This is important to perform this here, as the previous EnterExclusive/EnterShared
                    // could have failed (e.g trying to lock exclusive on a file already locked)
                    // and thus, we would have to release the lock of the parent in finally
                    if ((flags & (FindNodeFlags.KeepParentNodeExclusive | FindNodeFlags.KeepParentNodeShared)) != 0)
                    {
                        parentNode = null;
                        break;
                    }
                }
                else
                {
                    // Going down the directory, 
                    nextParent = subNode as FsDirectoryNode;
                    if (nextParent != null && !IsNodeAlreadyLocked(nextParent, existingNodes))
                    {
                        EnterExclusiveOrSharedDirectoryOrBlock(nextParent, path, isRequiringExclusiveLockForParent);
                        isNextParentLockTaken = true;
                    }
                }
            }
            finally
            {
                // We unlock the parent only if it was taken
                if (isParentLockTaken && parentNode != null)
                {
                    ExitExclusiveOrShared(parentNode, isRequiringExclusiveLockForParent);
                }
            }
            parentNode = nextParent;
            isParentLockTaken = isNextParentLockTaken;
        }
        return result;
    }
    private static bool IsNodeAlreadyLocked(FsDirectoryNode directoryNode, NodeResult[] existingNodes)
    {
        foreach (var existingNode in existingNodes)
        {
            if (existingNode.Directory == directoryNode || existingNode.Node == directoryNode)
            {
                return true;
            }
        }
        return false;
    }
    //private FileSystemEventDispatcher<Watcher> GetOrCreateDispatcher()
    //{
    //    lock (_dispatcherLock)
    //    {
    //        _dispatcher ??= new FileSystemEventDispatcher<Watcher>(this);
    //        return _dispatcher;
    //    }
    //}
    //private FileSystemEventDispatcher<Watcher>? TryGetDispatcher()
    //{
    //    lock (_dispatcherLock)
    //    {
    //        return _dispatcher;
    //    }
    //}
    // ----------------------------------------------
    // Locks internals
    // ----------------------------------------------
    private void EnterFileSystemShared()
    {
        nodeLock.EnterShared(RootDirectory.Path);
    }
    private void ExitFileSystemShared()
    {
        nodeLock.ExitShared();
    }
    private void EnterFileSystemExclusive()
    {
        nodeLock.EnterExclusive();
    }
    private void ExitFileSystemExclusive()
    {
        nodeLock.ExitExclusive();
    }
    private void EnterSharedDirectoryOrBlock(FsDirectoryNode node, Path context)
    {
        EnterShared(node, context, true, FileShare.Read);
    }
    private void EnterExclusiveOrSharedDirectoryOrBlock(FsDirectoryNode node, Path context, bool isExclusive)
    {
        if (isExclusive)
        {
            EnterExclusiveDirectoryOrBlock(node, context);
        }
        else
        {
            EnterSharedDirectoryOrBlock(node, context);
        }
    }
    private void EnterExclusiveDirectoryOrBlock(FsDirectoryNode node, Path context)
    {
        EnterExclusive(node, context, true);
    }
    private void EnterExclusive(FsNode node, Path context)
    {
        EnterExclusive(node, context, node is FsDirectoryNode);
    }
    private void EnterShared(FsNode node, Path context, FileShare share)
    {
        EnterShared(node, context, node is FsDirectoryNode, share);
    }
    private void EnterShared(FsNode node, Path context, bool block, FileShare share)
    {
        if (block)
        {
            node.EnterShared(share, context);
        }
        else if (!node.TryEnterShared(share))
        {
            var pathType = node is FsFileNode ? "file" : "directory";
            throw new IOException($"The {pathType} `{context}` is already used for writing by another thread.");
        }
    }
    internal void ExitShared(FsNode node)
    {
        node.ExitShared();
    }
    private void EnterExclusive(FsNode node, Path context, bool block)
    {
        if (block)
        {
            node.EnterExclusive();
        }
        else if (!node.TryEnterExclusive())
        {
            var pathType = node is FsFileNode ? "file" : "directory";
            throw new IOException($"The {pathType} `{context}` is already locked.");
        }
    }
    private void ExitExclusiveOrShared(FsNode node, bool isExclusive)
    {
        if (isExclusive)
        {
            node.ExitExclusive();
        }
        else
        {
            node.ExitShared();
        }
    }
    internal void ExitExclusive(FsNode node)
    {
        node.ExitExclusive();
    }
    private void TryLockExclusive(FsNode node, FsNodeList locks, bool recursive, Path context)
    {
        if (locks is null) throw new ArgumentNullException(nameof(locks));
        if (node is FsDirectoryNode directory)
        {
            if (recursive)
            {
                foreach (var child in directory.Children)
                {
                    EnterExclusive(child.Value, context);
                    var path = context + child.Key;
                    locks.Add(child);
                    TryLockExclusive(child.Value, locks, true, path);
                }
            }
            else
            {
                if (directory.Children.Count > 0)
                {
                    throw new IOException($"The directory `{context}` is not empty");
                }
            }
        }
    }
}
