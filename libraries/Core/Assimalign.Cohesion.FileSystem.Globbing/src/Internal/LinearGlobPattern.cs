using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

using static System.IO.Glob;

internal abstract class LinearGlobPattern : GlobPattern<LinearGlobPattern.FrameData>
{
    public LinearGlobPattern(Glob glob, StringComparison comparison)
    {
        Glob = glob;
        Comparison = comparison;
    }


    protected StringComparison Comparison { get; }
    public override Glob Glob { get; }
    public override bool Test(IFileSystemFile file)
    {
        if (IsStackEmpty())
        {
            throw new InvalidOperationException();// SR.CannotTestFile);
        }

        if (!Frame.IsNotApplicable && IsLastSegment() && TestMatchingSegment(file.Name))
        {
            return true;
        }

        return false;
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
            Segment segment = Glob[Frame.SegmentIndex];

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
        return Frame.SegmentIndex == Glob.Count - 1;
    }
    protected bool TestMatchingSegment(string value)
    {
        if (Frame.SegmentIndex >= Glob.Count)
        {
            return false;
        }

        return Glob[Frame.SegmentIndex].IsMatch(value, Comparison);
    }
}
