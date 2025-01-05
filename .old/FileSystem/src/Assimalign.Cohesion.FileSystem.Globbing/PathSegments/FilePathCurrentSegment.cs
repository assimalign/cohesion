namespace Assimalign.Cohesion.FileSystem.Globbing.PathSegments;

public class FilePathCurrentSegment : IFilePathSegment
{
    public bool CanProduceStem { get { return false; } }

    public bool Match(string value)
    {
        return false;
    }
}
