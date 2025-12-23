using System.IO;

namespace Assimalign.Cohesion.FileSystem;

public enum FileSystemEventType
{
    Created = WatcherChangeTypes.Created,
    Deleted = WatcherChangeTypes.Deleted,
    Changed = WatcherChangeTypes.Changed,
    Renamed = WatcherChangeTypes.Renamed
}