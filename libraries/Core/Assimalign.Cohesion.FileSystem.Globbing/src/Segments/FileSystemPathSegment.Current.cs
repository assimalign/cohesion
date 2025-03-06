using System;

namespace Assimalign.Cohesion.FileSystem;

public abstract partial class FileSystemPathSegment
{
    internal partial class CurrentSegment : FileSystemPathSegment
    {
        public override bool HasStem => false;
        public override string Value { get; } = ".";
        public override PathSegmentKind Kind => PathSegmentKind.Current;
        public override bool Match(string value, StringComparison comparison)
        {
            return false;
        }
    }
}