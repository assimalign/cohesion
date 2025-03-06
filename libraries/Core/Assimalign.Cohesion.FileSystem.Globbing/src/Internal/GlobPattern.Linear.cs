namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class LinearGlobPattern : ILinearGlobPattern
{
    public LinearGlobPattern(FileSystemPathSegment[] segments)
    {
        Segments = segments;
    }

    public FileSystemPathSegment[] Segments { get; }
}
