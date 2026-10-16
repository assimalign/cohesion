using System;
using System.Runtime.InteropServices;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

/// <summary>Gate 6 acceptance evidence for metadata edits over multi-GiB lazy payloads.</summary>
public sealed class BmffBoundedSaveTests
{
    private const long allocationBudget = 256 * 1024;
    private readonly ITestOutputHelper _output;

    /// <summary>Captures allocation measurements in the test result.</summary>
    /// <param name="output">The test output recorder.</param>
    public BmffBoundedSaveTests(ITestOutputHelper output) { _output = output; }

    /// <summary>Copies the complete unchanged payload within one size-independent allocation budget.</summary>
    /// <param name="payloadGiB">The virtual payload size in GiB.</param>
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Save: ISO/IEC 14496-12 4.2, 8.10.1 bounded allocation at 4 and 40 GiB")]
    [InlineData(4)]
    [InlineData(40)]
    public void Save_MetadataEditOverVirtualPayload_UsesFixedAllocationBudget(int payloadGiB)
    {
        // Arrange
        var payloadLength = payloadGiB * 1024L * 1024 * 1024;
        using var source = BoundedSaveFixture.CreateSource(payloadLength);
        using var document = BmffDocument.Load(source);
        var payload = ((MediaDataBox)document.Boxes[0]).Payload;
        document.Boxes.Insert(0, new UserDataBox(0, 0));
        var expectedLength = source.Length + 8;
        source.BytesRead.ShouldBeLessThan(16384);
        var bytesReadBefore = source.BytesRead;
        using var output = new BoundedSaveFixture.CountingSink();

        // Act: synchronous save keeps both samples on the same thread. Fixture setup and
        // assertions are excluded; no warm-up save, forced collection or payload-copy shortcut.
        var before = GC.GetAllocatedBytesForCurrentThread();
        document.Save(output);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        _output.WriteLine($"Bmff {RuntimeInformation.ProcessArchitecture}: {payloadGiB} GiB, allocated={allocated} B, budget={allocationBudget} B, output={output.BytesWritten} B");
        output.BytesWritten.ShouldBe(expectedLength);
        allocated.ShouldBeLessThan(allocationBudget);
        output.MaximumWrite.ShouldBeLessThanOrEqualTo(131072);
        payload.Length.ShouldBe(payloadLength);
        payload.IsMaterialized.ShouldBeFalse();
        (source.BytesRead - bytesReadBefore).ShouldBe(payloadLength);
        source.MaximumRead.ShouldBeLessThanOrEqualTo(131072);
    }
}
