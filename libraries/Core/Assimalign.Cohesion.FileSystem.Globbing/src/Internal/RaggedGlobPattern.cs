using System;
using System.IO;
using System.Collections.Generic;
using static System.IO.Glob;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;


internal abstract class RaggedGlobPattern : GlobPattern<RaggedGlobPattern.FrameData>
{
    public RaggedGlobPattern(
        Glob glob, 
        Segment[] startsWith, 
        Segment[][] contains, 
        Segment[] endsWiths, 
        StringComparison comparison)
    {
        Glob = glob;
        StartsWith = startsWith;
        Contains = contains;
        EndsWith = endsWiths;
        Comparison = comparison;
    }

    public Segment[] EndsWith { get; }
    public Segment[] StartsWith { get; }
    public Segment[][] Contains { get; }
    protected StringComparison Comparison { get; }
    public override Glob Glob { get; }
    public override bool Test(IFileSystemFile file)
    {
        if (IsStackEmpty())
        {
            throw new InvalidOperationException();// SR.CannotTestFile);
        }

        if (!Frame.IsNotApplicable && IsEndingGroup() && TestMatchingGroup(file))
        {
            return true;
        }

        return false;
    }

    public sealed override void PushDirectory(IFileSystemDirectory directory)
    {
        // copy the current frame
        FrameData frame = Frame;

        if (IsStackEmpty())
        {
            // initializing
            frame.SegmentGroupIndex = -1;
            frame.SegmentGroup = StartsWith;
        }
        else if (Frame.IsNotApplicable)
        {
            // no change
        }
        else if (IsStartingGroup())
        {
            if (!TestMatchingSegment(directory.Name))
            {
                // nothing down this path is affected by this pattern
                frame.IsNotApplicable = true;
            }
            else
            {
                // starting path incrementally satisfied
                frame.SegmentIndex += 1;
            }
        }
        else if (!IsStartingGroup() && directory.Name == "..")
        {
            // any parent path segment is not applicable in **
            frame.IsNotApplicable = true;
        }
        else if (!IsStartingGroup() && !IsEndingGroup() && TestMatchingGroup(directory))
        {
            frame.SegmentIndex = Frame.SegmentGroup.Length;
            frame.BacktrackAvailable = 0;
        }
        else
        {
            // increase directory backtrack length
            frame.BacktrackAvailable += 1;
        }

        if (frame.InStem)
        {
            frame.StemItems.Add(directory.Name);
        }

        while (
            frame.SegmentIndex == frame.SegmentGroup.Length &&
            frame.SegmentGroupIndex != Contains.Length)
        {
            frame.SegmentGroupIndex += 1;
            frame.SegmentIndex = 0;
            if (frame.SegmentGroupIndex < Contains.Length)
            {
                frame.SegmentGroup = Contains[frame.SegmentGroupIndex];
            }
            else
            {
                frame.SegmentGroup = EndsWith;
            }

            // We now care about the stem
            frame.InStem = true;
        }

        PushDataFrame(frame);
    }

    public override void PopDirectory()
    {
        base.PopDirectory();
        if (Frame.StemItems.Count > 0)
        {
            Frame.StemItems.RemoveAt(Frame.StemItems.Count - 1);
        }
    }

    protected bool IsStartingGroup()
    {
        return Frame.SegmentGroupIndex == -1;
    }

    protected bool IsEndingGroup()
    {
        return Frame.SegmentGroupIndex == Contains.Length;
    }

    protected bool TestMatchingSegment(string value)
    {
        if (Frame.SegmentIndex >= Frame.SegmentGroup.Length)
        {
            return false;
        }
        return Frame.SegmentGroup[Frame.SegmentIndex].IsMatch(value, Comparison);
    }

    protected bool TestMatchingGroup(IFileSystemInfo value)
    {
        int groupLength = Frame.SegmentGroup.Length;
        int backtrackLength = Frame.BacktrackAvailable + 1;

        if (backtrackLength < groupLength)
        {
            return false;
        }

        var scan = value;

        for (int index = 0; index != groupLength; ++index)
        {
            Segment segment = Frame.SegmentGroup[groupLength - index - 1];

            if (scan is IFileSystemDirectory dir && !segment.IsMatch(dir.Name, Comparison))
            {
                return false;
            }
            if (scan is IFileSystemFile file && !segment.IsMatch(file.Name, Comparison))
            {
                return false;
            }
            if (scan is IFileSystemDirectory dir1)
            {
                scan = dir1.Parent;
            }
            if (scan is IFileSystemFile file1)
            {
                scan = file1.Directory.Parent;
            }
        }
        return true;
    }


    public struct FrameData
    {
        public bool IsNotApplicable;

        public int SegmentGroupIndex;

        public Segment[] SegmentGroup;

        public int BacktrackAvailable;

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
}
