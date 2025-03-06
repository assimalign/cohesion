using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem;

public abstract partial class FileSystemPathSegment
{
    internal partial class RecursiveWildcardSegment : FileSystemPathSegment
    {
        public override string Value { get; } = "**";
        public override bool HasStem => true;
        public override PathSegmentKind Kind => PathSegmentKind.RecursiveWildcard;
        public override bool Match(string value, StringComparison comparison)
        {
            return false;
        }
    }
}