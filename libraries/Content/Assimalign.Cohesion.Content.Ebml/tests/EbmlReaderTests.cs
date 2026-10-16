using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlReaderTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - ReadNext: iterates typed values with absolute headers")]
    public void ReadNext_TypedTree_ReturnsValuesAndOffsets()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        using var bytes = new MemoryStream();
        document.Save(bytes);
        bytes.Position = 0;
        using var reader = EbmlReader.Create(bytes, document.Schema);

        // Act / Assert
        reader.ReadNext().ShouldBeTrue();
        reader.Current.Offset.ShouldBe(0);
        reader.Current.Identifier.EncodedValue.ShouldBe(0x1A45DFA3UL);
        reader.Skip();
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();
        reader.ReadUnsignedInteger().ShouldBe(ulong.MaxValue);
        reader.ReadNext().ShouldBeTrue();
        reader.ReadSignedInteger().ShouldBe(-129);
        reader.ReadNext().ShouldBeTrue();
        reader.ReadFloat().ShouldBe(1.25);
        reader.ReadNext().ShouldBeTrue();
        reader.ReadAsciiString().ShouldBe("text");
        reader.ReadNext().ShouldBeTrue();
        reader.ReadUtf8String().ShouldBe("café");
        reader.ReadNext().ShouldBeTrue();
        reader.ReadDate().ShouldBe(-1);
        reader.ReadNext().ShouldBeTrue();
        var binary = reader.ReadBinary();
        binary.IsMaterialized.ShouldBeFalse();
        // Reading a borrowed segment moves the shared cursor; do it only after the scan.
        reader.ReadNext().ShouldBeTrue();
        reader.Current.Type.ShouldBe(EbmlElementType.Binary);
        reader.Skip();
        reader.ReadNext().ShouldBeFalse();
        reader.LeaveMaster();
        reader.ReadNext().ShouldBeFalse();
        binary.ToArray(3).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - ReadNext: non-seekable unknown master ends before its sibling")]
    public void ReadNext_UnknownMasterOnNonSeekableInput_PreservesSibling()
    {
        // Arrange
        var bytes = EbmlTestData.Encode(writer =>
        {
            writer.BeginMaster(EbmlTestData.Id(0xA0));
            writer.BeginMaster(EbmlTestData.Id(0xA8));
            writer.WriteUnsignedInteger(EbmlTestData.Id(0xAA), 42);
            writer.EndMaster();
            writer.WriteUnsignedInteger(EbmlTestData.Id(0xA9), 7);
            writer.EndMaster();
        });
        using var source = new ForwardOnlyStream(new MemoryStream(bytes));
        using var reader = EbmlReader.Create(source, EbmlTestData.Schema());

        // Act / Assert
        reader.ReadNext().ShouldBeTrue();
        reader.Current.IsUnknownSize.ShouldBeTrue();
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();
        reader.ReadUnsignedInteger().ShouldBe(42UL);
        reader.ReadNext().ShouldBeFalse();
        reader.LeaveMaster();
        reader.ReadNext().ShouldBeTrue();
        reader.Current.Identifier.EncodedValue.ShouldBe(0xA9UL);
        reader.ReadUnsignedInteger().ShouldBe(7UL);
        reader.ReadNext().ShouldBeFalse();
        reader.LeaveMaster();
        reader.ReadNext().ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - ReadValue: preserves integer and date extrema")]
    [InlineData(long.MinValue)]
    [InlineData(-129)]
    [InlineData(-128)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(long.MaxValue)]
    public void ReadValue_SignedExtrema_RoundTrips(long value)
    {
        // Arrange
        var bytes = EbmlTestData.Encode(writer => writer.WriteSignedInteger(EbmlTestData.Id(0xA2), value));
        using var reader = EbmlReader.Create(new MemoryStream(bytes));

        // Act / Assert
        reader.ReadNext().ShouldBeTrue();
        reader.ReadSignedInteger().ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - ReadValue: empty scalar values have zero defaults")]
    [InlineData(EbmlElementType.UnsignedInteger)]
    [InlineData(EbmlElementType.SignedInteger)]
    [InlineData(EbmlElementType.Float)]
    [InlineData(EbmlElementType.Date)]
    public void ReadValue_EmptyScalar_ReturnsZero(EbmlElementType type)
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0x81, 0x80 }));
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        switch (type)
        {
            case EbmlElementType.UnsignedInteger: reader.ReadUnsignedInteger().ShouldBe(0UL); break;
            case EbmlElementType.SignedInteger: reader.ReadSignedInteger().ShouldBe(0L); break;
            case EbmlElementType.Float: reader.ReadFloat().ShouldBe(0d); break;
            case EbmlElementType.Date: reader.ReadDate().ShouldBe(0L); break;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - ReadValue: ignores bytes following a string NUL")]
    public void ReadValue_TerminatedString_IgnoresFollowingBytes()
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0x81, 0x85, 0x61, 0, 0xFF, 0, 0x62 }));
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        reader.ReadUtf8String().ShouldBe("a");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - ReadBinary: non-seekable materialization respects cap")]
    public void ReadBinary_NonSeekableOverCap_ReportsLimit()
    {
        // Arrange
        using var input = new ForwardOnlyStream(new MemoryStream(new byte[] { 0x81, 0x83, 1, 2, 3 }));
        using var reader = EbmlReader.Create(input, options: new EbmlReaderOptions { MaxBufferedValueLength = 2 });
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => reader.ReadBinary());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        error.Position.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - ReadNext: requires consumption before advancing")]
    public void ReadNext_UnreadPayload_RejectsAdvance()
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0x81, 0x80 }));
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => reader.ReadNext());
        reader.Skip();
        reader.ReadNext().ShouldBeFalse();
    }
}
