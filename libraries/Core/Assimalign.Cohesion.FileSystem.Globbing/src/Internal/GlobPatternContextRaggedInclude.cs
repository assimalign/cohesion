using System;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class GlobPatternContextRaggedInclude : GlobPatternContextRagged
{
    public GlobPatternContextRaggedInclude(IRaggedGlobPattern pattern, StringComparison comparison)
        : base(pattern, comparison)
    {
    }

    public override void Declare(Action<FileSystemPathSegment, bool> onDeclare)
    {
        if (IsStackEmpty())
        {
            throw new InvalidOperationException();// SR.CannotDeclarePathSegment);
        }

        if (Frame.IsNotApplicable)
        {
            return;
        }

        if (IsStartingGroup() && Frame.SegmentIndex < Frame.SegmentGroup.Length)
        {
            onDeclare(Frame.SegmentGroup[Frame.SegmentIndex], false);
        }
        else
        {
            onDeclare(FileSystemPathSegment.WildcardSegment.MatchAll, false);
        }
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

        if (IsStartingGroup() && !TestMatchingSegment(directory.Name))
        {
            // deterministic not-included
            return false;
        }

        return true;
    }
}
