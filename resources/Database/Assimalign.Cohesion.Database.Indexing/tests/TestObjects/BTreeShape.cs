using System.Collections.Generic;

using Shouldly;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

internal sealed class BTreeShape
{
    private BTreeShape()
    {
    }

    internal int Height { get; private set; }

    internal List<int> LeafCounts { get; } = new();

    internal long UsedLeafBytes { get; private set; }

    internal long AvailableLeafBytes { get; private set; }

    internal double Fill => (double)UsedLeafBytes / AvailableLeafBytes;

    internal static BTreeShape Read(Storage.Storage storage, long root)
    {
        var shape = new BTreeShape();
        var leaves = new List<long>();
        var visited = new HashSet<long>();

        void Visit(long page, int depth)
        {
            visited.Add(page).ShouldBeTrue("each tree page is reached once");
            using var handle = storage.PageManager.GetPage(page);
            var node = new BTreeNode(handle.Page.AsBodySpan());
            if (node.IsLeaf)
            {
                if (shape.Height == 0)
                {
                    shape.Height = depth;
                }
                depth.ShouldBe(shape.Height, "every leaf has the same depth");
                leaves.Add(page);
                shape.LeafCounts.Add(node.EntryCount);
                shape.AvailableLeafBytes += node.Body.Length - 32;
                for (int i = 0; i < node.EntryCount; i++)
                {
                    shape.UsedLeafBytes += BTreeNode.LeafEntrySize(node.GetKey(i).Length) + BTreeNode.DirectorySlotSize;
                }
                return;
            }

            Visit(node.LeftmostChild, depth + 1);
            for (int i = 0; i < node.EntryCount; i++)
            {
                Visit(node.GetChildAt(i), depth + 1);
            }
        }

        Visit(root, 1);
        for (int i = 0; i < leaves.Count; i++)
        {
            using var handle = storage.PageManager.GetPage(leaves[i]);
            var node = new BTreeNode(handle.Page.AsBodySpan());
            node.PrevLeaf.ShouldBe(i == 0 ? -1 : leaves[i - 1]);
            node.NextLeaf.ShouldBe(i == leaves.Count - 1 ? -1 : leaves[i + 1]);
        }
        return shape;
    }
}
