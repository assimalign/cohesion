using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;
using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlComplianceTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §5: MakeId refuses zero and reserved four-octet data")]
    [InlineData(0U)]
    [InlineData(0x0FFFFFFFU)]
    [InlineData(uint.MaxValue)]
    public void MakeId_InvalidIdentifierData_RejectsValue(uint value)
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => VInt.MakeId(value));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§7.1–7.2: integer widths zero through eight")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void ReadValue_EveryIntegerWidth_PreservesUnsignedAndSignExtension(int width)
    {
        // Arrange
        var payload = new byte[width];
        Array.Fill(payload, (byte)0xFF);
        var bytes = Rfc8794Corpus.Element(0x81, payload);
        using var unsigned = EbmlReader.Create(new MemoryStream(bytes));
        using var signed = EbmlReader.Create(new MemoryStream(bytes));
        unsigned.ReadNext().ShouldBeTrue();
        signed.ReadNext().ShouldBeTrue();

        // Act / Assert
        var expected = width == 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1;
        unsigned.ReadUnsignedInteger().ShouldBe(expected);
        signed.ReadSignedInteger().ShouldBe(width == 0 ? 0L : -1L);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §7.3: floating encodings preserve negative zero and non-finite values")]
    [InlineData(0x8000000000000000UL)]
    [InlineData(0x7FF0000000000000UL)]
    [InlineData(0xFFF0000000000000UL)]
    [InlineData(0x7FF8000000001234UL)]
    public void Save_FloatingWireBits_PreservesUntouchedPayload(ulong bits)
    {
        // Arrange
        var payload = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(payload, bits);
        var bytes = Rfc8794Corpus.HeaderDocument(Rfc8794Corpus.Element(0x4282, "test"u8.ToArray()),
            Rfc8794Corpus.Element(0xA0, Rfc8794Corpus.Element(0xA3, payload)));
        using var source = new MemoryStream(bytes);

        // Act
        using var document = EbmlDocument.Load(source, EbmlTestData.Schema());
        using var saved = new MemoryStream();
        document.Save(saved);

        // Assert
        saved.ToArray().ShouldBe(bytes);
        unchecked((ulong)BitConverter.DoubleToInt64Bits((double)document.Body.Elements[0].Children[0].Value!)).ShouldBe(bits);
    }

    public static IEnumerable<object[]> IdentifierCases => Rfc8794Corpus.Identifiers();
    public static IEnumerable<object[]> InvalidValueCases => Rfc8794Corpus.InvalidValues();
    public static IEnumerable<object[]> HeaderCases => Rfc8794Corpus.Headers();

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §5: reserved and shortest-form element IDs")]
    [MemberData(nameof(IdentifierCases))]
    public void ReadNext_IdentifierCorpus_EnforcesIdentifierRules(string rule, ulong encoded, bool valid)
    {
        // Arrange
        using var bytes = new MemoryStream();
        VInt.FromEncoded(encoded).Write(bytes);
        VInt.EncodeSize(0).Write(bytes);
        bytes.Position = 0;
        using var reader = EbmlReader.Create(bytes, options: new EbmlReaderOptions { MaxIdLength = 8 });

        // Act / Assert
        VInt.FromEncoded(encoded).IsValidIdentifier.ShouldBe(valid, rule);
        if (valid)
        {
            reader.ReadNext().ShouldBeTrue(rule);
            reader.ReadBinary().IsEmpty.ShouldBeTrue();
        }
        else
        {
            var error = Should.Throw<ContentFormatException>(() => reader.ReadNext());
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure, rule);
            error.Position.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4,6.1: over-long size VINTs remain valid")]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void ReadNext_ExplicitSizeWidths_PreservesWidth(int width)
    {
        // Arrange
        var bytes = Rfc8794Corpus.Element(0x81, [], width);
        using var reader = EbmlReader.Create(new ChunkedReadStream(new NonSeekableStream(new MemoryStream(bytes)), 1));

        // Act / Assert
        reader.ReadNext().ShouldBeTrue();
        reader.Current.SizeLength.ShouldBe(width);
        reader.Current.DataSize.ShouldBe(0);
        reader.Skip();
        reader.ReadNext().ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6.2: all-ones sizes are only for eligible masters")]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void ReadNext_AllOnesSize_ResolvesUnknownMaster(int width)
    {
        // Arrange
        using var bytes = new MemoryStream();
        VInt.FromEncoded(0xA0).Write(bytes);
        VInt.UnknownSize(width).Write(bytes);
        var valid = bytes.ToArray();
        var invalid = (byte[])valid.Clone();
        invalid[0] = 0x81;

        // Act / Assert
        using var reader = EbmlReader.Create(new NonSeekableStream(new MemoryStream(valid)), EbmlTestData.Schema());
        reader.ReadNext().ShouldBeTrue();
        reader.Current.IsUnknownSize.ShouldBeTrue();
        reader.Skip();
        using var leafReader = EbmlReader.Create(new MemoryStream(invalid));
        Should.Throw<ContentFormatException>(() => leafReader.ReadNext()).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§7.1–7.6: invalid widths and encodings have classified errors")]
    [MemberData(nameof(InvalidValueCases))]
    public void ReadValue_InvalidValueCorpus_ReportsInvalidValue(string rule, EbmlElementType type, byte[] payload)
    {
        // Arrange
        var schema = new EbmlSchemaBuilder().Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x81), "Value", type)).Build();
        using var reader = EbmlReader.Create(new MemoryStream(Rfc8794Corpus.Element(0x81, payload)), schema);

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => { reader.ReadNext(); reader.ReadValue(); });
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue, rule);
        error.Position.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §11.2: header schema documents")]
    [MemberData(nameof(HeaderCases))]
    public void Load_HeaderCorpus_EnforcesHeaderRules(string rule, byte[] bytes, ContentFormatErrorCode expected)
    {
        // Arrange
        using var source = new MemoryStream(bytes);

        // Act / Assert
        if (expected == ContentFormatErrorCode.Unspecified)
        {
            using var document = EbmlDocument.Load(source, EbmlTestData.Schema());
            document.Header.DocType.ShouldBe("test", rule);
            using var output = new MemoryStream();
            document.Save(output);
            output.ToArray().ShouldBe(bytes, rule);
        }
        else
        {
            var error = Should.Throw<ContentFormatException>(() => EbmlDocument.Load(source, EbmlTestData.Schema()));
            error.ErrorCode.ShouldBe(expected, rule);
            error.Position.ShouldNotBeNull();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–6: truncation at every header byte uses shared corpus")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadNext_HeaderPrefixes_ReportsTruncation(bool nonSeekable)
    {
        // Arrange — a four-byte ID and eight-byte size exercises all twelve header boundaries.
        var bytes = Rfc8794Corpus.Element(0x1A45DFA4, [], 8);

        // Act / Assert
        TruncationCorpus.AssertPrefixes(bytes, prefix =>
        {
            using var memory = new MemoryStream(prefix.ToArray());
            using Stream source = nonSeekable ? new NonSeekableStream(memory, leaveOpen: true) : memory;
            using var reader = EbmlReader.Create(source);
            return reader.ReadNext();
        }, allowEmpty: true);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4,11.2: async header truncations report absolute positions")]
    public async Task ReadAsync_HeaderPrefixes_ReportsTruncation()
    {
        // Arrange
        var bytes = Rfc8794Corpus.HeaderDocument(Rfc8794Corpus.Element(0x4282, "test"u8.ToArray()));

        // Act / Assert
        for (var length = 0; length < 5; length++)
        {
            using var source = new AsyncOnlyStream(new MemoryStream(bytes.AsSpan(0, length).ToArray()));
            var error = await Should.ThrowAsync<ContentFormatException>(() =>
                EbmlDocument.CreateReader(EbmlTestData.Schema()).ReadAsync(source, CancellationToken.None).AsTask());
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
            error.Position.ShouldBe(length);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6.2: globals and unknown IDs do not terminate masters")]
    public void ReadNext_GlobalAndUnknownChildren_EndsAtSchemaSibling()
    {
        // Arrange
        byte[] bytes = [0xA0, 0xFF, 0xA8, 0xFF, 0xB1, 0x80, 0xEC, 0x80, 0xAA, 0x80, 0xA9, 0x80];
        using var reader = EbmlReader.Create(new NonSeekableStream(new MemoryStream(bytes)), EbmlTestData.Schema());

        // Act / Assert
        reader.ReadNext().ShouldBeTrue();
        reader.Skip();
        reader.Position.ShouldBe(bytes.Length);
        reader.ReadNext().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6.2: known parent boundary ends an unknown child")]
    public void ReadNext_UnknownChildAtParentEnd_ClosesBothMasters()
    {
        // Arrange
        using var reader = EbmlReader.Create(new NonSeekableStream(new MemoryStream(new byte[] { 0xA0, 0x84, 0xA8, 0xFF, 0xAA, 0x80 })),
            EbmlTestData.Schema());

        // Act / Assert
        reader.ReadNext().ShouldBeTrue();
        reader.Skip();
        reader.ReadNext().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §11.2.4: parsed header negotiates extended ID width")]
    public void Load_HeaderDeclaresFiveByteIds_UsesDeclaredLimit()
    {
        // Arrange
        var id = VInt.FromEncoded(0x080FFFFFFFUL);
        var schema = new EbmlSchemaBuilder().Include(EbmlSchema.Header)
            .Add(EbmlElementDefinition.Create(id, "WideRoot", EbmlElementType.MasterElement)).Build();
        using var document = EbmlDocument.Create(schema);
        document.Header.EBMLMaxIDLength = 5;
        document.Body.Elements.Add(EbmlElement.Create(id, EbmlElementType.MasterElement));
        using var source = new MemoryStream();
        document.Save(source);
        source.Position = 0;

        // Act
        using var loaded = EbmlDocument.Load(source, schema);

        // Assert
        loaded.Body.Elements[0].Identifier.Length.ShouldBe(5);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§6.1,7.8: 40 GiB payload stays lazy")]
    public void Load_FortyGiBPayload_ReadsOnlyHeaders()
    {
        // Arrange
        const long payloadSize = 40L * 1024 * 1024 * 1024;
        using var prefix = new MemoryStream();
        prefix.Write(Rfc8794Corpus.Element(0x1A45DFA3, Rfc8794Corpus.Element(0x4282, "test"u8.ToArray())));
        var binarySize = VInt.EncodeSize((ulong)payloadSize);
        VInt.FromEncoded(0xA0).Write(prefix);
        VInt.EncodeSize((ulong)(payloadSize + 1 + binarySize.Length)).Write(prefix);
        VInt.FromEncoded(0xA7).Write(prefix);
        binarySize.Write(prefix);
        using var source = new SparsePayloadStream(prefix.ToArray(), payloadSize);

        // Act
        using var document = EbmlDocument.Load(source, EbmlTestData.Schema(), new EbmlReaderOptions { MaxBufferedValueLength = 16 });
        var segment = (BinarySegment)document.Body.Elements[0].Children[0].Value!;

        // Assert
        segment.Length.ShouldBe(payloadSize);
        segment.IsMaterialized.ShouldBeFalse();
        source.BytesRead.ShouldBe(prefix.Length);
    }
}
