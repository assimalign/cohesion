using System;

namespace Assimalign.Cohesion.FileSystem;

public abstract partial class FileSystemPathSegment
{
    internal partial class ParentSegment : FileSystemPathSegment
    {
        public override bool HasStem => false;
        public override string Value { get; } = "..";
        public override PathSegmentKind Kind => PathSegmentKind.ParentDirectory;
        public override bool Match(string value, StringComparison comparison)
        {
            return string.Equals(Value, value, comparison);
        }
    }
}

