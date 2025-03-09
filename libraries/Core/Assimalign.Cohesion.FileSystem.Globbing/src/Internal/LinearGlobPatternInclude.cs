using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

using static System.IO.Glob;

internal class LinearGlobPatternInclude : LinearGlobPattern
{
    public LinearGlobPatternInclude(Glob pattern, StringComparison comparison)
        : base(pattern, comparison)
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

        if (Frame.SegmentIndex < Glob.Count)
        {
            onDeclare(Glob[Frame.SegmentIndex], IsLastSegment());
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
