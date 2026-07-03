using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Tests;

public class StreamContentTests
{
    [Fact(DisplayName = "Cohesion Test [Content] - Stream: seekable source is reopenable and rewinds per read")]
    public void OpenRead_SeekableSource_RewindsPerRead()
    {
        byte[] data = [1, 2, 3];
        var source = new TrackingStream(data);
        using var content = ContentFactory.FromStream(source);

        content.CanReopen.ShouldBeTrue();
        content.Length.ShouldBe(3);

        using (var first = content.OpenRead())
        {
            ReadAll(first).ShouldBe(data);
        }

        using var second = content.OpenRead();
        ReadAll(second).ShouldBe(data);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Stream: non-seekable source is single-use")]
    public void OpenRead_NonSeekableSource_SecondReadThrows()
    {
        var source = new TrackingStream([1, 2, 3], seekable: false);
        using var content = ContentFactory.FromStream(source);

        content.CanReopen.ShouldBeFalse();
        content.Length.ShouldBeNull();

        using (var first = content.OpenRead())
        {
            ReadAll(first).ShouldBe([1, 2, 3]);
        }

        Should.Throw<ContentException>(content.OpenRead);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Stream: owned source is disposed with the content")]
    public void Dispose_OwnedSource_DisposesSource()
    {
        var source = new TrackingStream([1]);
        var content = ContentFactory.FromStream(source);

        content.Dispose();

        source.Disposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Stream: borrowed source is left open on dispose")]
    public void Dispose_BorrowedSource_LeavesSourceOpen()
    {
        var source = new TrackingStream([1]);
        var content = ContentFactory.FromStream(source, leaveOpen: true);

        content.Dispose();

        source.Disposed.ShouldBeFalse();
        source.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Stream: disposing a read view does not dispose the source")]
    public void DisposeReadView_DoesNotDisposeSource()
    {
        var source = new TrackingStream([1, 2]);
        using var content = ContentFactory.FromStream(source);

        var view = content.OpenRead();
        view.Dispose();

        source.Disposed.ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => view.ReadByte());
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Stream: read views are read-only")]
    public void ReadView_Write_Throws()
    {
        using var content = ContentFactory.FromStream(new TrackingStream([1]));
        using var view = content.OpenRead();

        view.CanWrite.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => view.Write([1], 0, 1));
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Stream: async dispose honors ownership")]
    public async Task DisposeAsync_OwnedSource_DisposesSource()
    {
        var source = new TrackingStream([1]);
        var content = ContentFactory.FromStream(source);

        await content.DisposeAsync();

        source.Disposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Stream: unreadable source is rejected by the factory")]
    public void FromStream_UnreadableSource_Throws()
    {
        var unreadable = new MemoryStream();
        unreadable.Dispose();

        Should.Throw<ArgumentException>(() => ContentFactory.FromStream(unreadable));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
