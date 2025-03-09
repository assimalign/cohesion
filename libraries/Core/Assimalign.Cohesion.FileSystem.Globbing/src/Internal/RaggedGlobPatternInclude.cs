using System;
using System.IO;
using static System.IO.Glob;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class RaggedGlobPatternInclude : RaggedGlobPattern
{
    public RaggedGlobPatternInclude(Glob glob,
        Segment[] startsWith,
        Segment[][] contains,
        Segment[] endsWiths,
        StringComparison comparison)
        : base(glob, startsWith, contains, endsWiths, comparison)
    {
    }

    public override void Declare(Action<Segment, bool> onDeclare)
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
            // TODO: Fix
            //onDeclare(FileSystemPathSegment.WildcardSegment.MatchAll, false);
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
