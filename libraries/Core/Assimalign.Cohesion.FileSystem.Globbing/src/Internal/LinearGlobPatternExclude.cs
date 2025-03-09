using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

using static System.IO.Glob;

internal class LinearGlobPatternExclude : LinearGlobPattern
{
    public LinearGlobPatternExclude(Glob glob, StringComparison comparison)
        : base(glob, comparison)
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
