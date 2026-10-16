using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlPayloadStreamTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6: OpenPayloadStream: partial reads drain exactly to the next sibling")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReadNext_PartialPayload_DrainsAndInvalidates(bool disposeFirst, bool seekable)
    {
        // Arrange — fixture bytes built in code: binary leaf followed by a binary sibling.
        using var memory = new MemoryStream(new byte[] { 0x81, 0x83, 1, 2, 3, 0x82, 0x81, 4 });
        using Stream source = seekable ? memory : new ForwardOnlyStream(memory);
        using var reader = EbmlReader.Create(source, options: new EbmlReaderOptions { MaxBufferedValueLength = 0 });
        reader.ReadNext().ShouldBeTrue();
        using var payload = reader.OpenPayloadStream();
        payload.CanSeek.ShouldBeFalse();
        payload.ReadByte().ShouldBe(1);
        memory.Position.ShouldBe(3);
        Should.Throw<InvalidOperationException>(() => reader.OpenPayloadStream());
        Should.Throw<InvalidOperationException>(() => reader.ReadValue());
        if (disposeFirst) { payload.Dispose(); }

        // Act
        reader.ReadNext().ShouldBeTrue();

        // Assert
        reader.Current.Identifier.EncodedValue.ShouldBe(0x82UL);
        reader.Position.ShouldBe(7);
        Should.Throw<ObjectDisposedException>(() => payload.ReadByte());
        using var sibling = reader.OpenPayloadStream();
        sibling.ReadByte().ShouldBe(4);
        sibling.ReadByte().ShouldBe(-1);
        reader.ReadNext().ShouldBeFalse();
        source.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6: OpenPayloadStream: oversized reads never cross the element")]
    public void OpenPayloadStream_NonSeekablePayload_StopsAtBoundary()
    {
        // Arrange
        using var memory = new MemoryStream(new byte[] { 0x81, 0x83, 1, 2, 3, 0x82, 0x80 });
        using var source = new ForwardOnlyStream(memory);
        using var reader = EbmlReader.Create(source);
        reader.ReadNext().ShouldBeTrue();
        using var payload = reader.OpenPayloadStream();

        // Act
        using var copied = new MemoryStream();
        payload.CopyTo(copied);

        // Assert
        copied.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        memory.Position.ShouldBe(5);
        payload.Position.ShouldBe(3);
        reader.Position.ShouldBe(5);
        reader.Skip();
        reader.ReadNext().ShouldBeTrue();
        reader.Current.Offset.ShouldBe(5);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§6,11.3.1: OpenPayloadStream: CRC observes streamed and drained bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_PartialPayloadAndCrc_Verifies(bool tamper)
    {
        // Arrange — CRC fixture is synthesized by the independent reference CRC implementation.
        var bytes = CrcFixture.Master(0xA0, new byte[] { 0xA7, 0x83, 1, 2, 3 });
        if (tamper) { bytes[^1] ^= 1; }
        using var source = new ForwardOnlyStream(new MemoryStream(bytes));
        using var reader = EbmlReader.Create(source, EbmlTestData.Schema(), new EbmlReaderOptions { VerifyCrc32 = true });
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();
        using (var crc = reader.OpenPayloadStream()) { crc.ReadByte().ShouldNotBe(-1); }
        reader.ReadNext().ShouldBeTrue();

        // Act
        using var payload = reader.OpenPayloadStream();
        var first = new byte[1];
        (await payload.ReadAsync(first, CancellationToken.None)).ShouldBe(1);
        reader.ReadNext().ShouldBeFalse();

        // Assert
        first[0].ShouldBe((byte)1);
        if (tamper)
        {
            Should.Throw<ContentFormatException>(() => reader.LeaveMaster()).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        }
        else
        {
            reader.LeaveMaster();
            reader.Diagnostics.ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6: OpenPayloadStream: truncated payloads report absolute positions")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadNext_TruncatedPayload_ReportsTruncation(bool drain)
    {
        // Arrange — declared size exceeds available data; enclosing slices supply an absolute origin.
        using var memory = new MemoryStream(new byte[] { 0, 0, 0x81, 0x84, 1, 2 });
        using var window = ContentSliceStream.Create(memory, 2, 4);
        using var source = ContentSliceStream.CreateForwardOnly(window, 4);
        using var reader = EbmlReader.Create(source);
        reader.ReadNext().ShouldBeTrue();
        using var payload = reader.OpenPayloadStream();

        // Act / Assert
        var error = drain
            ? Should.Throw<ContentFormatException>(() => reader.ReadNext())
            : Should.Throw<ContentFormatException>(() => payload.CopyTo(Stream.Null));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(6);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6: OpenPayloadStream: unknown size requires master traversal")]
    public void OpenPayloadStream_UnknownMaster_RejectsWithoutConsuming()
    {
        // Arrange
        using var source = new ForwardOnlyStream(new MemoryStream(new byte[] { 0xA0, 0xFF, 0xA7, 0x80 }));
        using var reader = EbmlReader.Create(source, EbmlTestData.Schema());
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => reader.OpenPayloadStream());
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();
        using var payload = reader.OpenPayloadStream();
        payload.ReadByte().ShouldBe(-1);
        reader.ReadNext().ShouldBeFalse();
        reader.LeaveMaster();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6: OpenPayloadStream: cancellation leaves payload resumable and reader disposal invalidates it")]
    public async Task ReadAsync_CancelledPayload_PreservesCursor()
    {
        // Arrange
        using var memory = new MemoryStream(new byte[] { 0x81, 0x83, 1, 2, 3 });
        using var source = new ForwardOnlyStream(memory);
        using var reader = EbmlReader.Create(source);
        reader.ReadNext().ShouldBeTrue();
        using var payload = reader.OpenPayloadStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => payload.ReadAsync(new byte[1], cancellation.Token).AsTask());
        reader.Position.ShouldBe(2);
        payload.ReadByte().ShouldBe(1);
        reader.Dispose();
        Should.Throw<ObjectDisposedException>(() => payload.ReadByte());
        memory.CanRead.ShouldBeTrue();
        memory.Position.ShouldBe(3);
    }
}
