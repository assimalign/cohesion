using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal abstract class GlobPatternContextLinear
   : GlobPatternContext<GlobPatternContextLinear.FrameData>
{
    private readonly StringComparison _comparison;

    public GlobPatternContextLinear(ILinearGlobPattern pattern, StringComparison comparison)
    {
        Pattern = pattern;
        _comparison = comparison;
    }

    protected ILinearGlobPattern Pattern { get; }

    public override GlobPatternTestResult Test(IFileSystemFile file)
    {
        if (IsStackEmpty())
        {
            throw new InvalidOperationException();// SR.CannotTestFile);
        }

        if (!Frame.IsNotApplicable && IsLastSegment() && TestMatchingSegment(file.Name))
        {
            return GlobPatternTestResult.Success(CalculateStem(file));
        }

        return GlobPatternTestResult.Failed;
    }
    public override void PushDirectory(IFileSystemDirectory directory)
    {
        // copy the current frame
        FrameData frame = Frame;

        if (IsStackEmpty() || Frame.IsNotApplicable)
        {
            // when the stack is being initialized
            // or no change is required.
        }
        else if (!TestMatchingSegment(directory.Name))
        {
            // nothing down this path is affected by this pattern
            frame.IsNotApplicable = true;
        }
        else
        {
            // Determine this frame's contribution to the stem (if any)
            FileSystemPathSegment segment = Pattern.Segments[Frame.SegmentIndex];
            if (frame.InStem || segment.HasStem)
            {
                frame.InStem = true;
                frame.StemItems.Add(directory.Name);
            }

            // directory matches segment, advance position in pattern
            frame.SegmentIndex = frame.SegmentIndex + 1;
        }

        PushDataFrame(frame);
    }

    public partial struct FrameData
    {
        public bool IsNotApplicable;
        public int SegmentIndex;
        public bool InStem;
        private IList<string> _stemItems;

        public IList<string> StemItems
        {
            get { return _stemItems ?? (_stemItems = new List<string>()); }
        }

        public string? Stem
        {
            get { return _stemItems == null ? null : string.Join("/", _stemItems); }
        }
    }
    protected bool IsLastSegment()
    {
        return Frame.SegmentIndex == Pattern.Segments.Length - 1;
    }
    protected bool TestMatchingSegment(string value)
    {
        if (Frame.SegmentIndex >= Pattern.Segments.Length)
        {
            return false;
        }

        return Pattern.Segments[Frame.SegmentIndex].Match(value, _comparison);
    }
    protected string CalculateStem(IFileSystemFile matchedFile)
    {
        return GlobMatcherContext.CombinePath(Frame!.Stem!, matchedFile.Name);
    }
}
