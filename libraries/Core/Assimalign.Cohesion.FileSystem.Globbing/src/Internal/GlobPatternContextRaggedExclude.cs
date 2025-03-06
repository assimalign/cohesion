using System;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class GlobPatternContextRaggedExclude : GlobPatternContextRagged
{
    public GlobPatternContextRaggedExclude(IRaggedGlobPattern pattern, StringComparison comparison)
        : base(pattern, comparison)
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

        if (Pattern.EndsWith.Length == 0 &&
            Frame.SegmentGroupIndex == Pattern.Contains.Length - 1 &&
            TestMatchingGroup(directory))
        {
            // directory excluded by matching up to final '/**'
            return true;
        }

        return false;
    }
}
