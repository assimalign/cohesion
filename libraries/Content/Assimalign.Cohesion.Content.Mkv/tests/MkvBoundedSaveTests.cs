using System;
using System.Runtime.InteropServices;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

/// <summary>Gate 6 acceptance evidence for metadata edits over multi-GiB lazy payloads.</summary>
public sealed class MkvBoundedSaveTests
{
    private const long allocationBudget = 512 * 1024;
    private readonly ITestOutputHelper _output;

    /// <summary>Captures allocation measurements in the test result.</summary>
    /// <param name="output">The test output recorder.</param>
    public MkvBoundedSaveTests(ITestOutputHelper output) { _output = output; }

    /// <summary>Copies the complete unchanged payload within one size-independent allocation budget.</summary>
    /// <param name="payloadGiB">The virtual payload size in GiB.</param>
    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - Save: RFC 9559 §6.2 and RFC 8794 §15 bounded allocation at 4 and 40 GiB")]
    [InlineData(4)]
    [InlineData(40)]
    public void Save_MetadataEditOverVirtualPayload_UsesFixedAllocationBudget(int payloadGiB)
    {
        // Arrange
        var payloadLength = payloadGiB * 1024L * 1024 * 1024;
        using var source = BoundedSaveFixture.CreateSource(payloadLength);
        using var document = MkvDocument.Load(source);
        var cluster = document.Segment.Children[^1];
        var payload = (BinarySegment)cluster.Children[^1].Value!;
        // Equal UTF-8 width preserves the live segment layout while changing its title.
        MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1549A966), 0x7BA9).Value = "Bounded title";
        var expectedLength = source.Length;
        source.BytesRead.ShouldBeLessThan(16384);
        var bytesReadBefore = source.BytesRead;
        using var output = new BoundedSaveFixture.CountingSink();

        // Act: synchronous save keeps both samples on the same thread. Fixture setup and
        // assertions are excluded; no warm-up save, forced collection or payload-copy shortcut.
        var before = GC.GetAllocatedBytesForCurrentThread();
        document.Save(output);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        _output.WriteLine($"Mkv {RuntimeInformation.ProcessArchitecture}: {payloadGiB} GiB, allocated={allocated} B, budget={allocationBudget} B, output={output.BytesWritten} B");
        output.BytesWritten.ShouldBe(expectedLength);
        allocated.ShouldBeLessThan(allocationBudget);
        output.MaximumWrite.ShouldBeLessThanOrEqualTo(131072);
        payload.Length.ShouldBe(payloadLength + 4);
        payload.IsMaterialized.ShouldBeFalse();
        (source.BytesRead - bytesReadBefore).ShouldBeLessThan(payloadLength + 16384);
        (source.BytesRead - bytesReadBefore).ShouldBeGreaterThanOrEqualTo(payloadLength);
    }
}
