
using System;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.FileSystem;

public class FileSystemEvent
{
    public FileSystemEvent(FileSystemPath path, object? state, FileSystemEventType eventType)
    {
        ArgumentException.ThrowIfEnumNotDefined(eventType);
        Path = path;
        State = state;
        EventType = eventType;
    }

    /// <summary>
    /// 
    /// </summary>
    public object? State { get; }

    /// <summary>
    /// The path that triggered the event.
    /// </summary>
    public FileSystemPath Path { get; }

    /// <summary>
    /// 
    /// </summary>
    public FileSystemEventType EventType { get; }
}