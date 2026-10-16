using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Tests;

public class CompositeContentTests
{
    [Fact(DisplayName = "Cohesion Test [Content] - Composite: items are enumerated in order")]
    public async Task GetItemsAsync_Composite_ReturnsItemsInOrder()
    {
        var first = MemoryContent.Create(new byte[] { 1 }, name: "first");
        var second = MemoryContent.Create(new byte[] { 2 }, name: "second");
        using var composite = CompositeContent.Create([first, second], name: "pair");

        var names = new List<string?>();
        await foreach (var item in composite.GetItemsAsync(CancellationToken.None))
        {
            names.Add(item.Name);
        }

        names.ShouldBe(["first", "second"]);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Composite: disposal propagates to owned items")]
    public void Dispose_OwnedItems_DisposesChildren()
    {
        var child = MemoryContent.Create(new byte[] { 1 });
        var composite = CompositeContent.Create([child]);

        composite.Dispose();

        Should.Throw<ObjectDisposedException>(child.OpenRead);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Composite: borrowed items are left undisposed")]
    public void Dispose_BorrowedItems_LeavesChildrenUsable()
    {
        using var child = MemoryContent.Create(new byte[] { 1 });
        var composite = CompositeContent.Create([child], leaveItemsOpen: true);

        composite.Dispose();

        using var read = child.OpenRead();
        read.ReadByte().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Composite: a pure composite has no serialized form")]
    public void OpenRead_PureComposite_Throws()
    {
        using var composite = CompositeContent.Create([]);

        composite.CanReopen.ShouldBeFalse();
        composite.Length.ShouldBeNull();
        Should.Throw<NotSupportedException>(() => composite.OpenRead());
    }
}
