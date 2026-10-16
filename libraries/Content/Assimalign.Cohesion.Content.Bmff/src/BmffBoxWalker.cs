using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Controls a generic box walk.</summary>
public enum BmffWalkAction
{
    /// <summary>Continues in depth-first, document order.</summary>
    Continue,
    /// <summary>Skips an entered node's children, but still calls leave for that node.</summary>
    SkipSubtree,
    /// <summary>Stops immediately without further enter or leave callbacks.</summary>
    Stop
}

/// <summary>Walks any BMFF or profile tree without recursive calls or payload I/O.</summary>
/// <remarks>Do not mutate the tree during traversal. Paths include the current node and are borrowed
/// read-only views valid only during the callback; copy a path to retain it. Root depth is zero.
/// Leave callbacks may stop the walk; SkipSubtree on leave is equivalent to Continue.</remarks>
public static class BmffBoxWalker
{
    /// <summary>Walks a box, including the root itself.</summary>
    /// <param name="box">The root box.</param>
    /// <param name="enter">Called before the children.</param>
    /// <param name="leave">Called after the children, or null.</param>
    /// <param name="maxDepth">Maximum inclusive depth; defaults to 64.</param>
    /// <returns>True if traversal completed; false if a callback stopped it.</returns>
    /// <exception cref="ArgumentNullException">The box or enter callback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
    /// <exception cref="ContentFormatException">A visited node is null, cyclic or exceeds the depth limit.</exception>
    public static bool Walk(BmffBox box,
        Func<BmffBox, int, IReadOnlyList<FourCC>, BmffWalkAction> enter,
        Func<BmffBox, int, IReadOnlyList<FourCC>, BmffWalkAction>? leave = null, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(box);
        return WalkCore(new[] { box }, enter, leave, maxDepth);
    }

    /// <summary>Walks every top-level box and its descendants in document order.</summary>
    /// <param name="document">The document.</param>
    /// <param name="enter">Called before the children.</param>
    /// <param name="leave">Called after the children, or null.</param>
    /// <param name="maxDepth">Maximum inclusive depth; defaults to 64.</param>
    /// <returns>True if traversal completed; false if a callback stopped it.</returns>
    /// <exception cref="ArgumentNullException">The document or enter callback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
    /// <exception cref="ContentFormatException">A visited node is null, cyclic or exceeds the depth limit.</exception>
    public static bool Walk(BmffDocument document,
        Func<BmffBox, int, IReadOnlyList<FourCC>, BmffWalkAction> enter,
        Func<BmffBox, int, IReadOnlyList<FourCC>, BmffWalkAction>? leave = null, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WalkCore(document.Boxes, enter, leave, maxDepth);
    }

    private static bool WalkCore(IList<BmffBox> roots,
        Func<BmffBox, int, IReadOnlyList<FourCC>, BmffWalkAction> enter,
        Func<BmffBox, int, IReadOnlyList<FourCC>, BmffWalkAction>? leave, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(enter);
        var cursor = new BmffTraversal(roots, maxDepth);
        while (cursor.MoveNext())
        {
            var action = cursor.IsLeaving
                ? leave?.Invoke(cursor.Box, cursor.Depth, cursor.Path) ?? BmffWalkAction.Continue
                : enter(cursor.Box, cursor.Depth, cursor.Path);
            if (action == BmffWalkAction.Stop) { return false; }
            if (!cursor.IsLeaving && action == BmffWalkAction.SkipSubtree) { cursor.SkipChildren = true; }
        }
        return true;
    }
}
