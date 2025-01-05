using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing.PatternContexts;

public abstract class FilePatternContextLinear
   : FilePatternContext<FilePatternContextLinear.FrameData>
{
    public FilePatternContextLinear(IFileLinearPattern pattern)
    {
        Pattern = pattern;
    }

    public override FilePatternTestResult Test(IFileSystemFile file)
    {
        if (IsStackEmpty())
        {
            throw new InvalidOperationException();// SR.CannotTestFile);
        }

        if (!Frame.IsNotApplicable && IsLastSegment() && TestMatchingSegment(file.Name))
        {
            return FilePatternTestResult.Success(CalculateStem(file));
        }

        return FilePatternTestResult.Failed;
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
            IFilePathSegment segment = Pattern.Segments[Frame.SegmentIndex];
            if (frame.InStem || segment.CanProduceStem)
            {
                frame.InStem = true;
                frame.StemItems.Add(directory.Name);
            }

            // directory matches segment, advance position in pattern
            frame.SegmentIndex = frame.SegmentIndex + 1;
        }

        PushDataFrame(frame);
    }

    public struct FrameData
    {
        public bool IsNotApplicable;
        public int SegmentIndex;
        public bool InStem;
        private IList<string> _stemItems;

        public IList<string> StemItems
        {
            get { return _stemItems ?? (_stemItems = new List<string>()); }
        }

        public string Stem
        {
            get { return _stemItems == null ? null : string.Join("/", _stemItems); }
        }
    }

    protected IFileLinearPattern Pattern { get; }

    protected bool IsLastSegment()
    {
        return Frame.SegmentIndex == Pattern.Segments.Count - 1;
    }

    protected bool TestMatchingSegment(string value)
    {
        if (Frame.SegmentIndex >= Pattern.Segments.Count)
        {
            return false;
        }

        return Pattern.Segments[Frame.SegmentIndex].Match(value);
    }

    protected string CalculateStem(IFileSystemInfo matchedFile)
    {
        return FileMatcherContext.CombinePath(Frame.Stem, matchedFile.Name);
    }
}
