using System;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class GlobPatternContextLinearInclude : GlobPatternContextLinear
{
    public GlobPatternContextLinearInclude(ILinearGlobPattern pattern, StringComparison comparison)
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

        if (Frame.SegmentIndex < Pattern.Segments.Length)
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
