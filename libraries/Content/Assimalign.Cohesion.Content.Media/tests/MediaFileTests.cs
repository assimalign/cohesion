using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Media.Tests;

public class MediaFileTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Media] - Media kinds retain the binary content contract")]
    [InlineData(MediaFileKind.Video)]
    [InlineData(MediaFileKind.Audio)]
    [InlineData(MediaFileKind.Image)]
    [InlineData(MediaFileKind.Other)]
    public async Task OpenReadAsync_MediaContract_ReturnsReopenableBinaryContent(MediaFileKind kind)
    {
        // Arrange
        using var media = new MemoryMediaFile(kind);
        IBinaryFile binary = media;
        var bytes = new byte[3];
        // Act
        await using (var stream = await binary.OpenReadAsync(CancellationToken.None))
        {
            await stream.ReadContentExactlyAsync(bytes, CancellationToken.None);
        }
        using var reopened = binary.OpenRead();
        // Assert
        media.Kind.ShouldBe(kind);
        binary.CanReopen.ShouldBeTrue();
        binary.Length.ShouldBe(3);
        bytes.ShouldBe(new byte[] { 1, 2, 3 });
        reopened.ReadByte().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Media files expose ordered child content")]
    public async Task GetItemsAsync_MediaContract_ExposesItsContent()
    {
        // Arrange
        using var media = new MemoryMediaFile(MediaFileKind.Video);
        IComposableContent composite = media;
        var count = 0;
        // Act
        await foreach (var item in composite.GetItemsAsync(CancellationToken.None))
        {
            using var stream = await item.OpenReadAsync(CancellationToken.None);
            stream.ReadByte().ShouldBe(1);
            count++;
        }
        // Assert
        count.ShouldBe(1);
    }
}
