using System;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlCrcTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§11.3.1: CRC-32: checks all sibling bytes on seekable and streaming input")]
    [InlineData(false)]
    [InlineData(true)]
    public void Skip_ValidCrc_VerifiesWithoutMasterBuffer(bool forwardOnly)
    {
        // Arrange
        CrcFixture.Compute(Encoding.ASCII.GetBytes("123456789")).ShouldBe(0xCBF43926U);
        var bytes = CrcFixture.Master(0xA0, new byte[] { 0xA1, 0x81, 7 });
        using var memory = new MemoryStream(bytes);
        using Stream source = forwardOnly ? new ForwardOnlyStream(memory) : memory;
        using var reader = EbmlReader.Create(source, EbmlTestData.Schema(), new EbmlReaderOptions { VerifyCrc32 = true, MaxBufferedValueLength = 1 });
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        reader.Skip();
        reader.ReadNext().ShouldBeFalse();
        reader.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§11.3.1: CRC-32: mismatches throw or become lenient diagnostics")]
    [InlineData(false)]
    [InlineData(true)]
    public void Skip_TamperedPayload_ReportsMismatch(bool lenient)
    {
        // Arrange
        var bytes = CrcFixture.Master(0xA0, new byte[] { 0xA1, 0x81, 7 });
        bytes[^1] ^= 1;
        using var reader = EbmlReader.Create(new ForwardOnlyStream(new MemoryStream(bytes)), EbmlTestData.Schema(),
            new EbmlReaderOptions { VerifyCrc32 = true, Lenient = lenient });
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        if (lenient)
        {
            reader.Skip();
            reader.Diagnostics.Count.ShouldBe(1);
            reader.Diagnostics[0].ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
            reader.Diagnostics[0].Position.ShouldBe(4);
        }
        else
        {
            var error = Should.Throw<ContentFormatException>(() => reader.Skip());
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
            error.Position.ShouldBe(4);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§11.3.1: CRC-32: nested unknown masters exclude their sibling lookahead")]
    public void Skip_NestedUnknownCrc_AccountsForEachParent()
    {
        // Arrange
        var inner = CrcFixture.Master(0xA8, new byte[] { 0xAA, 0x81, 5 }, unknown: true);
        var children = new byte[inner.Length + 3];
        inner.CopyTo(children, 0);
        new byte[] { 0xA9, 0x81, 9 }.CopyTo(children, inner.Length);
        var bytes = CrcFixture.Master(0xA0, children, unknown: true);
        using var reader = EbmlReader.Create(new ForwardOnlyStream(new MemoryStream(bytes)), EbmlTestData.Schema(),
            new EbmlReaderOptions { VerifyCrc32 = true });
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        reader.Skip();
        reader.ReadNext().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§11.3.1: CRC-32: an opt-out scan does not verify a checksum")]
    public void Skip_VerificationDisabled_IgnoresMismatch()
    {
        // Arrange
        var bytes = CrcFixture.Master(0xA0, new byte[] { 0xA1, 0x81, 7 });
        bytes[^1] ^= 1;
        using var reader = EbmlReader.Create(new MemoryStream(bytes), EbmlTestData.Schema());
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        reader.Skip();
        reader.ReadNext().ShouldBeFalse();
    }
}
