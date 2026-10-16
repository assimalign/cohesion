using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffMalformedTests
{
    public static IEnumerable<object[]> StrategicCases() => BmffModelTests.Cases();

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 4.2 every strategic header rejects truncation")]
    [MemberData(nameof(StrategicCases))]
    public void Load_TruncatedStrategicHeader_ReportsAbsolutePosition(string type, string clause)
    {
        // Arrange
        var valid = BmffModelFixtures.Write(BmffModelFixtures.Create(type));
        var leading = BmffFixtures.Box("free", [0x42]);
        for (var length = 1; length < 8; length++)
        {
            using var source = new MemoryStream(BmffFixtures.Join(leading, valid.AsSpan(0, length).ToArray()));
            // Act
            var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
            // Assert
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
            error.Position.ShouldBe(leading.Length + length);
        }
        clause.ShouldStartWith("ISO/IEC 14496-12");
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 strategic full-box prefixes reject truncation")]
    [MemberData(nameof(StrategicCases))]
    public void Load_TruncatedStrategicPrefix_ReportsFormatError(string type, string clause)
    {
        // Arrange: tref is an unprefixed container; its child header is truncated instead.
        var valid = BmffModelFixtures.Write(BmffModelFixtures.Create(type));
        var start = type == "tref" ? 1 : 0;
        var end = type == "tref" ? 8 : 4;
        for (var length = start; length < end; length++)
        {
            using var source = new MemoryStream(BmffFixtures.Box(type, valid.AsSpan(8, length).ToArray()));
            // Act
            var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
            // Assert
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
            error.Position!.Value.ShouldBeGreaterThanOrEqualTo(8L);
        }
        clause.ShouldStartWith("ISO/IEC 14496-12");
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 clauses 8.5-8.11 counts must fit payload")]
    [InlineData("stts", "8.6.1.2")]
    [InlineData("ctts", "8.6.1.3")]
    [InlineData("stss", "8.6.2")]
    [InlineData("stsc", "8.7.4")]
    [InlineData("stco", "8.7.5")]
    [InlineData("co64", "8.7.5")]
    [InlineData("stsz", "8.7.3.2")]
    [InlineData("stz2", "8.7.3.3")]
    [InlineData("subs", "8.7.7")]
    [InlineData("dref", "8.7.2")]
    [InlineData("stsd", "8.5.2")]
    [InlineData("elst", "8.6.6")]
    [InlineData("trun", "8.8.8")]
    [InlineData("sbgp", "8.9.2")]
    [InlineData("sgpd", "8.9.3")]
    [InlineData("tfra", "8.8.10")]
    [InlineData("iloc", "8.11.3")]
    [InlineData("iinf", "8.11.6")]
    public void Load_CountBeyondPayload_RejectsBeforeAllocation(string type, string clause)
    {
        // Arrange
        using var source = new MemoryStream(BmffMalformedFixtures.MissingEntries(type));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(source.Length);
        clause.ShouldNotBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.3.2 sample count overflow is bounded")]
    [InlineData(0u)]
    [InlineData(1u)]
    public void Load_SampleCountOverflow_ReportsLimitExceeded(uint sampleSize)
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("stsz", BmffMalformedFixtures.Words(0, sampleSize, uint.MaxValue)));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        error.Position.ShouldBe(20);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.6.6 v1 media time below -1 is invalid")]
    public void Load_NegativeEditMediaTime_ReportsInvalidValue()
    {
        // Arrange: duration 1, media_time -2, media_rate 1.0.
        using var source = new MemoryStream(BmffFixtures.Box("elst", BmffMalformedFixtures.Words(0x01000000, 1, 0, 1, uint.MaxValue, uint.MaxValue - 1, 0x10000)));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        error.Position.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 4.2 children cannot escape strategic containers")]
    [InlineData("moov", 0)]
    [InlineData("mvex", 0)]
    [InlineData("trak", 0)]
    [InlineData("tref", 0)]
    [InlineData("edts", 0)]
    [InlineData("mdia", 0)]
    [InlineData("minf", 0)]
    [InlineData("dinf", 0)]
    [InlineData("stbl", 0)]
    [InlineData("moof", 0)]
    [InlineData("traf", 0)]
    [InlineData("mfra", 0)]
    [InlineData("meta", 4)]
    [InlineData("udta", 0)]
    [InlineData("sinf", 0)]
    [InlineData("schi", 0)]
    public void Load_ImpossibleChildBounds_ReportsChildHeader(string type, int prefixSize)
    {
        // Arrange
        var child = BmffFixtures.Box("zzzz");
        BinaryPrimitives.WriteUInt32BigEndian(child, 9);
        using var source = new MemoryStream(BmffFixtures.Box(type, BmffFixtures.Join(new byte[prefixSize], child)));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(8 + prefixSize);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.8.8 fieldless trun count is limited")]
    public void Load_FieldlessRunCount_EnforcesConfiguredLimit()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("trun", BmffMalformedFixtures.Words(0, 3)));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source, new BmffReaderOptions { MaxTableEntryCount = 2 }));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.7 nested arrays share the decoded-byte limit")]
    public void Load_NestedSubsamples_EnforcesCumulativeBudget()
    {
        // Arrange: 16 wire bytes per entry expand to 28 decoded bytes per entry.
        var payload = new byte[8 + 160];
        payload[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 10);
        for (var i = 0; i < 10; i++)
        {
            var start = 8 + i * 16;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(start + 4), 1);
        }
        using var source = new MemoryStream(BmffFixtures.Box("subs", payload));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source, new BmffReaderOptions { MaxBufferedPayloadSize = 200 }));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.3.3 and 8.11.3 reject invalid widths")]
    [InlineData("stz2")]
    [InlineData("iloc")]
    public void Load_InvalidFieldWidth_ReportsInvalidValue(string type)
    {
        // Arrange
        var payload = type == "stz2" ? BmffMalformedFixtures.Words(0, 3, 0) : BmffMalformedFixtures.Words(0, 0xf0000000);
        using var source = new MemoryStream(BmffFixtures.Box(type, payload));
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
    }
}
