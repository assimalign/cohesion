using System.IO;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;

public class FileSystemEvent<T> : FileSystemEvent
{
    public FileSystemEvent(FileSystemPath path, T? state, FileSystemEventType eventType) : base(path, state, eventType)
    {
        State = state;
    }

    /// <summary>
    /// 
    /// </summary>
    public new T? State { get; }
}
