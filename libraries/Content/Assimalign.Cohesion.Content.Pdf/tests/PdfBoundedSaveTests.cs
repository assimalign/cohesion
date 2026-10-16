using System;
using System.Runtime.InteropServices;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

/// <summary>Gate 6 acceptance evidence for full and incremental PDF saves.</summary>
public sealed class PdfBoundedSaveTests
{
    private const long allocationBudget = 256 * 1024;
    private readonly ITestOutputHelper _output;

    /// <summary>Captures allocation measurements in the test result.</summary>
    /// <param name="output">The test output recorder.</param>
    public PdfBoundedSaveTests(ITestOutputHelper output) { _output = output; }

    /// <summary>Edits /Info and copies a multi-GiB stream within a fixed allocation budget.</summary>
    /// <param name="payloadGiB">The virtual payload size in GiB.</param>
    /// <param name="incremental">Whether to append a revision or rewrite the document.</param>
    /// <param name="expectedOverhead">The exact framing and xref bytes in the saved fixture.</param>
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - Write: ISO 32000-2 §§7.3.8, 7.5.6, 7.5.8 bounded full and incremental saves")]
    [InlineData(4, false, 854)]
    [InlineData(40, false, 856)]
    [InlineData(4, true, 916)]
    [InlineData(40, true, 923)]
    public void Write_InfoEditOverVirtualPayload_UsesFixedAllocationBudget(int payloadGiB, bool incremental, int expectedOverhead)
    {
        // Arrange
        var payloadLength = payloadGiB * 1024L * 1024 * 1024;
        using var source = BoundedSaveFixture.CreateSource(payloadLength);
        using var document = PdfReader.Create(new() { StartXrefSearchBytes = 4096 }).Read(source);
        var payload = document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>().Data;
        var info = new PdfDictionary();
        info[PdfName.Create("Title"u8)] = PdfString.Create("bounded save"u8);
        document.Trailer[PdfName.Create("Info"u8)] = PdfIndirectReference.Create(document.Add(info));
        var writer = PdfWriter.Create(new() { Incremental = incremental, UseCrossReferenceStream = true });
        source.BytesRead.ShouldBeLessThan(65536);
        var bytesReadBefore = source.BytesRead;
        using var output = new BoundedSaveFixture.CountingSink();

        // Act: synchronous write keeps both samples on the same thread, including lazy resolution
        // of remaining objects. Setup, result logging and assertions are outside the measurement.
        var before = GC.GetAllocatedBytesForCurrentThread();
        writer.Write(output, document);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        _output.WriteLine($"PDF {(incremental ? "incremental" : "full")} {RuntimeInformation.ProcessArchitecture}: {payloadGiB} GiB, allocated={allocated} B, budget={allocationBudget} B, output={output.BytesWritten} B, overhead={output.BytesWritten - payloadLength} B");
        // Golden lengths include the fixed ID, retained original xref object, and the writer's
        // compressed xref rows. Decimal offset widths differ between the two payload sizes.
        output.BytesWritten.ShouldBe(payloadLength + expectedOverhead);
        allocated.ShouldBeLessThan(allocationBudget);
        output.MaximumWrite.ShouldBeLessThanOrEqualTo(131072);
        source.MaximumRead.ShouldBeLessThanOrEqualTo(131072);
        payload.Length.ShouldBe(payloadLength);
        payload.IsMaterialized.ShouldBeFalse();
        (source.BytesRead - bytesReadBefore).ShouldBeGreaterThanOrEqualTo(payloadLength);
        (source.BytesRead - bytesReadBefore).ShouldBeLessThan(payloadLength + 65536);
    }
}
