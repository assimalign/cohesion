using System;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class GlobPatternContextLinearExclude : GlobPatternContextLinear
{
    public GlobPatternContextLinearExclude(ILinearGlobPattern pattern, StringComparison comparison)
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

        return IsLastSegment() && TestMatchingSegment(directory.Name);
    }
}
