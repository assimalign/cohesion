namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class RaggedGlobPattern : IRaggedGlobPattern
{
    public RaggedGlobPattern(
        FileSystemPathSegment[] segments,
        FileSystemPathSegment[] startwWith,
        FileSystemPathSegment[] endsWith, 
        FileSystemPathSegment[][] contains)
    {
        Segments = segments;
        StartsWith = startwWith;
        Contains = contains;
        EndsWith = endsWith;
    }

    public FileSystemPathSegment[] Segments { get; }
    public FileSystemPathSegment[] EndsWith { get; }
    public FileSystemPathSegment[] StartsWith { get; }
    public FileSystemPathSegment[][] Contains { get; }
}
