using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing.PatternContexts;

public class FilePatternContextLinearInclude : FilePatternContextLinear
{
    public FilePatternContextLinearInclude(IFileLinearPattern pattern)
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

        if (Frame.SegmentIndex < Pattern.Segments.Count)
        {
            onDeclare(Pattern.Segments[Frame.SegmentIndex], IsLastSegment());
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

        return !IsLastSegment() && TestMatchingSegment(directory.Name);
    }
}
