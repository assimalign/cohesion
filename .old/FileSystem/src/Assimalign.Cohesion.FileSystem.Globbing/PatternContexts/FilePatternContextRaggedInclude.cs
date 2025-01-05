using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing.PatternContexts;

using Assimalign.Cohesion.FileSystem.Globbing.PathSegments;

public class FilePatternContextRaggedInclude : FilePatternContextRagged
{
    public FilePatternContextRaggedInclude(IFileRaggedPattern pattern)
        : base(pattern)
    {
    }

    public override void Declare(Action<IFilePathSegment, bool> onDeclare)
    {
        if (IsStackEmpty())
        {
            throw new InvalidOperationException();// SR.CannotDeclarePathSegment);
        }

        if (Frame.IsNotApplicable)
        {
            return;
        }

        if (IsStartingGroup() && Frame.SegmentIndex < Frame.SegmentGroup.Count)
        {
            onDeclare(Frame.SegmentGroup[Frame.SegmentIndex], false);
        }
        else
        {
            onDeclare(FilePathWildcardSegment.MatchAll, false);
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
