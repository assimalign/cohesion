using System.IO;

namespace Assimalign.Cohesion.FileSystem;

public class FileSystemRenameEvent<T> : FileSystemEvent<T>
{
    public FileSystemRenameEvent(FileSystemPath oldPath, FileSystemPath newPath, T? state, FileSystemEventType eventType)
        : base(oldPath, state, eventType)
    {
        NewPath = newPath;
    }

    /// <summary>
    /// Returns the original path before the rename operation.
    /// </summary>
    public FileSystemPath OldPath => Path;

    /// <summary>
    /// Returns the new path after the rename operation.
    /// </summary>
    public FileSystemPath NewPath { get; }
}
