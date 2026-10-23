using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// One frame per ancestor, never one per sibling. Also shared by lazy queries.
[global::System.Diagnostics.DebuggerDisplay("BmffTraversal")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BmffTraversal
{
    private readonly IList<BmffBox> _roots;
    private readonly int _maxDepth;
    private readonly List<(BmffBox Box, int NextChild)> _frames = new();
    private readonly List<FourCC> _path = new();
    private readonly HashSet<BmffBox> _ancestors = new(ReferenceEqualityComparer.Instance);
    private int _rootIndex;

    internal BmffTraversal(IList<BmffBox> roots, int maxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        _roots = roots;
        _maxDepth = maxDepth;
        Path = _path.AsReadOnly();
    }

    internal BmffBox Box => _frames[^1].Box;
    internal int Depth => _frames.Count - 1;
    internal IReadOnlyList<FourCC> Path { get; }
    internal bool IsLeaving { get; private set; }
    internal bool SkipChildren { get; set; }

    internal bool MoveNext()
    {
        if (IsLeaving)
        {
            _ancestors.Remove(Box);
            _frames.RemoveAt(_frames.Count - 1);
            _path.RemoveAt(_path.Count - 1);
            IsLeaving = false;
        }
        if (_frames.Count == 0)
        {
            if (_rootIndex == _roots.Count) { return false; }
            Enter(_roots[_rootIndex++]);
            return true;
        }
        var frame = _frames[^1];
        if (!SkipChildren && frame.Box is BmffBoxComposite composite && frame.NextChild < composite.Children.Count)
        {
            _frames[^1] = (frame.Box, frame.NextChild + 1);
            Enter(composite.Children[frame.NextChild]);
        }
        else
        {
            IsLeaving = true;
            SkipChildren = false;
        }
        return true;
    }

    private void Enter(BmffBox box)
    {
        if (box is null || !_ancestors.Add(box))
        {
            throw new ContentFormatException(ErrorMessages.InvalidTree, ContentFormatErrorCode.InvalidStructure, box?.Offset ?? 0);
        }
        if (_frames.Count > _maxDepth)
        {
            throw new ContentFormatException(ErrorMessages.DepthLimit, ContentFormatErrorCode.LimitExceeded, box.Offset);
        }
        _frames.Add((box, 0));
        _path.Add(box.BoxType.ToFourCC());
    }

    private sealed class DebugView
    {
        public DebugView(BmffTraversal value)
        {
        }
    }
}
