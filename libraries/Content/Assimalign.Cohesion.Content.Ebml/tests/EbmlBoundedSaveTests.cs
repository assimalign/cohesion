using System;
using System.Runtime.InteropServices;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

/// <summary>Gate 6 acceptance evidence for metadata edits over multi-GiB lazy payloads.</summary>
public sealed class EbmlBoundedSaveTests
{
    private const long allocationBudget = 256 * 1024;
    private readonly ITestOutputHelper _output;

    /// <summary>Captures allocation measurements in the test result.</summary>
    /// <param name="output">The test output recorder.</param>
    public EbmlBoundedSaveTests(ITestOutputHelper output) { _output = output; }

    /// <summary>Copies the complete unchanged payload within one size-independent allocation budget.</summary>
    /// <param name="payloadGiB">The virtual payload size in GiB.</param>
    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - Save: RFC 8794 §§6.1, 7.7, 7.8 bounded allocation at 4 and 40 GiB")]
    [InlineData(4)]
    [InlineData(40)]
    public void Save_MetadataEditOverVirtualPayload_UsesFixedAllocationBudget(int payloadGiB)
    {
        // Arrange
        var payloadLength = payloadGiB * 1024L * 1024 * 1024;
        using var source = BoundedSaveFixture.CreateSource(payloadLength);
        using var document = EbmlDocument.Load(source, EbmlTestData.Schema());
        var root = document.Body.Elements[0];
        var payload = (BinarySegment)root.Children[0].Value!;
        root.Children.Insert(0, EbmlElement.Create(VInt.FromEncoded(0xA5), EbmlElementType.Utf8String, "bounded save"));
        // One-byte ID, one-byte size, twelve UTF-8 title bytes; root size width is unchanged.
        var expectedLength = source.Length + 14;
        source.BytesRead.ShouldBeLessThan(16384);
        var bytesReadBefore = source.BytesRead;
        using var output = new BoundedSaveFixture.CountingSink();

        // Act: synchronous save keeps both samples on the same thread. Fixture setup and
        // assertions are excluded; no warm-up save, forced collection or payload-copy shortcut.
        var before = GC.GetAllocatedBytesForCurrentThread();
        document.Save(output);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        _output.WriteLine($"Ebml {RuntimeInformation.ProcessArchitecture}: {payloadGiB} GiB, allocated={allocated} B, budget={allocationBudget} B, output={output.BytesWritten} B");
        output.BytesWritten.ShouldBe(expectedLength);
        allocated.ShouldBeLessThan(allocationBudget);
        output.MaximumWrite.ShouldBeLessThanOrEqualTo(131072);
        payload.Length.ShouldBe(payloadLength);
        payload.IsMaterialized.ShouldBeFalse();
        (source.BytesRead - bytesReadBefore).ShouldBe(payloadLength + 4);
    }
}
