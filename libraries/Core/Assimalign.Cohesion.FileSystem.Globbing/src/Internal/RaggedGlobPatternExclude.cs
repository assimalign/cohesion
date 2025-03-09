using System;
using System.IO;
using static System.IO.Glob;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class RaggedGlobPatternExclude : RaggedGlobPattern
{
    public RaggedGlobPatternExclude(
        Glob glob, 
        Segment[] startsWith, 
        Segment[][] contains, 
        Segment[] endsWiths, 
        StringComparison comparison)
        : base(glob, startsWith, contains, endsWiths, comparison)
    {
    }

    public override bool Test(IFileSystemDirectory directory)
    {
        if (IsStackEmpty())
        {
            throw new InvalidOperationException();// SR.CannotTestDirectory);
        }

        if (Frame.IsNotApplicable)
        {
            return false;
        }

        if (IsEndingGroup() && TestMatchingGroup(directory))
        {
            // directory excluded with file-like pattern
            return true;
        }

        if (EndsWith.Length == 0 &&
            Frame.SegmentGroupIndex == Contains.Length - 1 &&
            TestMatchingGroup(directory))
        {
            // directory excluded by matching up to final '/**'
            return true;
        }

        return false;
    }
}
