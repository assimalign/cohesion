using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffPrefixBufferTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Prefix: Should retain exact sync and async container bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_Prefix_ShouldPreserveBytes(bool prefixed)
    {
        // Arrange
        using BmffDocument document = new();
        document.Boxes.Add(prefixed ? new MetaBox(0, 0) : new MovieBox(0));
        byte[] expected = prefixed
            ? new byte[] { 0, 0, 0, 12, 109, 101, 116, 97, 0, 0, 0, 0 }
            : new byte[] { 0, 0, 0, 8, 109, 111, 111, 118 };
        using MemoryStream sync = new();
        using MemoryStream asyncOutput = new();

        // Act
        document.Save(sync);
        await document.SaveAsync(asyncOutput, CancellationToken.None);

        // Assert
        sync.ToArray().ShouldBe(expected);
        asyncOutput.ToArray().ShouldBe(expected);
    }
}
