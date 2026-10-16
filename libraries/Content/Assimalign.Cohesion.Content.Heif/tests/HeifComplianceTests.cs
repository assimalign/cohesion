using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

public class HeifComplianceTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Validate: ISO/IEC 14496-12 8.11.3/8.11.4/8.11.14 and 23008-12 grid constraints")]
    [InlineData("missing-property", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("outside-file", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("grid-count", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("hidden-primary", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("essential-unknown", ContentFormatErrorCode.Unsupported)]
    [InlineData("item-offset", ContentFormatErrorCode.Unsupported)]
    [InlineData("external-data", ContentFormatErrorCode.Unsupported)]
    [InlineData("protected", ContentFormatErrorCode.Unsupported)]
    [InlineData("duplicate-id", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("missing-primary", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("overflow-offset", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("exif-offset", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("cycle", ContentFormatErrorCode.InvalidStructure)]
    public void Validate_MalformedContainer_StrictFailsAndLenientDiagnoses(string variant, ContentFormatErrorCode code)
    {
        // Arrange
        var bytes = HeifMalformedFixtures.Create(variant);
        using var strictSource = new MemoryStream(bytes);
        using var lenientSource = new MemoryStream(bytes);

        // Act
        var error = Should.Throw<ContentFormatException>(() => HeifDocument.Load(strictSource, new() { Strict = true }));
        using var lenient = HeifDocument.Load(lenientSource);

        // Assert
        error.ErrorCode.ShouldBe(code);
        error.Position.ShouldNotBeNull();
        error.Position!.Value.ShouldBeGreaterThanOrEqualTo(0);
        lenient.Diagnostics.ShouldContain(x => x.ErrorCode == code);
        if (variant == "essential-unknown")
        {
            lenient.PrimaryItem!.IsSupported.ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Validate: ISO/IEC 14496-12 8.11.14 nonessential unknown properties remain usable")]
    public void Validate_NonessentialUnknown_PreservesAndSupports()
    {
        // Arrange
        var bytes = HeifMalformedFixtures.Mutate("heic", document =>
        {
            var meta = HeifFixtures.Find<MetaBox>(document.Boxes);
            var properties = HeifFixtures.Find<ItemPropertiesBox>(meta.Children);
            var ipco = HeifFixtures.Find<ItemPropertyContainerBox>(properties.Children);
            ipco.Children[2] = UnknownBox.Create(BmffBoxHeader.Create((BmffBoxType)0x7a7a7a7a, 1), BinarySegment.Create(new byte[] { 17 }));
        });
        using var source = new MemoryStream(bytes);

        // Act
        using var document = HeifDocument.Load(source, new() { Strict = true });
        using var output = new MemoryStream();
        document.Save(output);

        // Assert
        document.PrimaryItem!.IsSupported.ShouldBeTrue();
        document.Diagnostics.ShouldBeEmpty();
        output.ToArray().ShouldBe(bytes);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Brands: ISO/IEC 23008-12 brands and AVIF 6.2/6.3 distinguish stills and sequences")]
    [InlineData("mif1", true)]
    [InlineData("heic", true)]
    [InlineData("heix", true)]
    [InlineData("heim", true)]
    [InlineData("heis", true)]
    [InlineData("avif", true)]
    [InlineData("msf1", false)]
    [InlineData("avis", false)]
    [InlineData("avio", false)]
    public void Load_DeclaredBrand_ReportsRetainedScope(string brand, bool still)
    {
        // Arrange
        var bytes = HeifMalformedFixtures.Mutate("heic", document =>
        {
            var ftyp = HeifFixtures.Find<FileTypeBox>(document.Boxes);
            ftyp.MajorBrand = FourCC.Parse(brand).Value;
            ftyp.CompatibleBrands = [];
        });
        using var source = new MemoryStream(bytes);

        // Act / Assert
        if (still)
        {
            using var document = HeifDocument.Load(source, new() { Strict = true });
            document.PrimaryItem!.Id.ShouldBe(1u);
        }
        else
        {
            Should.Throw<ContentFormatException>(() => HeifDocument.Load(source, new() { Strict = true })).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Limits: ISO/IEC 14496-12 declared item/property/extent counts and 23008-12 grids are bounded")]
    [InlineData("items")]
    [InlineData("properties")]
    [InlineData("extents")]
    [InlineData("grid")]
    [InlineData("buffer")]
    [InlineData("depth")]
    public void Load_LimitExceeded_RejectsBeforeUnboundedAllocation(string limit)
    {
        // Arrange
        using var source = new MemoryStream(HeifFixtures.Create("grid"));
        var options = limit switch
        {
            "items" => new HeifReaderOptions { MaxItemCount = 1 },
            "properties" => new HeifReaderOptions { MaxPropertyCount = 1 },
            "extents" => new HeifReaderOptions { MaxExtentCount = 1 },
            "grid" => new HeifReaderOptions { MaxGridTileCount = 3 },
            "buffer" => new HeifReaderOptions { MaxBufferedPayloadSize = 4 },
            _ => new HeifReaderOptions { MaxDepth = 1 }
        };

        // Act / Assert
        Should.Throw<ContentFormatException>(() => HeifDocument.Load(source, options)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Read: ISO/IEC 14496-12 4.2 every HEIF box header truncation uses the linked TruncationCorpus")]
    public void Read_EveryHeaderPrefix_ReportsTruncated()
    {
        // Arrange
        foreach (var vector in HeifBoxFixtures.Boxes())
        {
            var bytes = HeifBoxTests.Write((BmffBox)vector[0]);
            var header = bytes.AsSpan(0, 8).ToArray();

            // Act / Assert
            TruncationCorpus.AssertPrefixes(header, prefix =>
            {
                using var source = new MemoryStream(prefix.ToArray());
                return BmffBoxHeader.TryRead(source, 0, null, true, out _);
            }, allowEmpty: true);
        }
        // Independently encode the large-size header path, not just eight-byte headers.
        byte[] large = [0, 0, 0, 1, 105, 100, 97, 116, 0, 0, 0, 0, 0, 0, 0, 16];
        TruncationCorpus.AssertPrefixes(large, prefix =>
        {
            using var source = new MemoryStream(prefix.ToArray());
            return BmffBoxHeader.TryRead(source, 0, null, true, out _);
        }, allowEmpty: true);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Read: ISO/IEC 14496-12 8.11.14.3 malicious ipma count fails before allocation")]
    public void Read_AssociationCountBomb_ReportsLimit()
    {
        // Arrange
        byte[] bytes = [0, 0, 0, 16, 105, 112, 109, 97, 0, 0, 0, 0, 255, 255, 255, 255];
        using var source = new MemoryStream(bytes);

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => BmffDocument.Load(source, registry: HeifBoxRegistry.Default));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        error.Position.ShouldBe(16L);
    }
}
