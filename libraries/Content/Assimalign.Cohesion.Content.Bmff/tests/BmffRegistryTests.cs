using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffRegistryTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Every deferred model preserves bytes")]
    [InlineData(BmffBoxType.BinaryXml)]
    [InlineData(BmffBoxType.ChunkOffset)]
    [InlineData(BmffBoxType.ChunkOffset6fBit)]
    [InlineData(BmffBoxType.Copyright)]
    [InlineData(BmffBoxType.DataReference)]
    [InlineData(BmffBoxType.DisposableSample)]
    [InlineData(BmffBoxType.EditList)]
    [InlineData(BmffBoxType.FecReservoir)]
    [InlineData(BmffBoxType.FileDeliveryItemInfo)]
    [InlineData(BmffBoxType.FileDeliverySessionGroup)]
    [InlineData(BmffBoxType.FilePartition)]
    [InlineData(BmffBoxType.GroupIdToName)]
    [InlineData(BmffBoxType.Handler)]
    [InlineData(BmffBoxType.HintMediaHeader)]
    [InlineData(BmffBoxType.IpmpControl)]
    [InlineData(BmffBoxType.IpmpInfo)]
    [InlineData(BmffBoxType.ItemInfo)]
    [InlineData(BmffBoxType.ItemLocation)]
    [InlineData(BmffBoxType.MediaHeader)]
    [InlineData(BmffBoxType.MetaRelationship)]
    [InlineData(BmffBoxType.MovieExtensionHeader)]
    [InlineData(BmffBoxType.MovieFragmentHeader)]
    [InlineData(BmffBoxType.MovieFragmentRandomAccessOffset)]
    [InlineData(BmffBoxType.MovieHeader)]
    [InlineData(BmffBoxType.NullMediaHeader)]
    [InlineData(BmffBoxType.OriginalFormat)]
    [InlineData(BmffBoxType.PrimaryItemReference)]
    [InlineData(BmffBoxType.ProgressiveDownloadInfo)]
    [InlineData(BmffBoxType.SampleCompositionTime)]
    [InlineData(BmffBoxType.SampleDecodingTime)]
    [InlineData(BmffBoxType.SampleDegradationPriority)]
    [InlineData(BmffBoxType.SampleDescription)]
    [InlineData(BmffBoxType.SampleGroupDescription)]
    [InlineData(BmffBoxType.SamplePaddingBits)]
    [InlineData(BmffBoxType.SampleSize)]
    [InlineData(BmffBoxType.CompactSampleSize)]
    [InlineData(BmffBoxType.SubSampleInformation)]
    [InlineData(BmffBoxType.SampleToChunk)]
    [InlineData(BmffBoxType.SampleToGroup)]
    [InlineData(BmffBoxType.SchemeType)]
    [InlineData(BmffBoxType.ShadowSyncSampleTable)]
    [InlineData(BmffBoxType.SoundMediaHeader)]
    [InlineData(BmffBoxType.SyncSampleTable)]
    [InlineData(BmffBoxType.TrackExtension)]
    [InlineData(BmffBoxType.TrackFragmentHeader)]
    [InlineData(BmffBoxType.TrackFragmentRandomAccess)]
    [InlineData(BmffBoxType.TrackFragmentRun)]
    [InlineData(BmffBoxType.TrackHeader)]
    [InlineData(BmffBoxType.TrackSelection)]
    [InlineData(BmffBoxType.VideoMediaHeader)]
    [InlineData(BmffBoxType.Xml)]
    public void Load_DeferredModel_PreservesPayload(BmffBoxType type)
    {
        // Arrange
        var bytes = BmffFixtures.Box(type.ToFourCC().ToString(), new byte[] { 0xff, 1, 2, 3 });
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        if (type == BmffBoxType.OriginalFormat)
        {
            document.Boxes[0].ShouldBeOfType<OriginalFormatBox>().DataFormat.Value.ShouldBe(0xff010203u);
        }
        else
        {
            document.Boxes[0].ShouldBeOfType<UnknownBox>().BoxType.ShouldBe(type);
        }
        output.ToArray().ShouldBe(bytes);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Every pure container reads and writes children")]
    [InlineData(BmffBoxType.AdditionalMeta)]
    [InlineData(BmffBoxType.DataInfo)]
    [InlineData(BmffBoxType.Edit)]
    [InlineData(BmffBoxType.Media)]
    [InlineData(BmffBoxType.MediaInfo)]
    [InlineData(BmffBoxType.Movie)]
    [InlineData(BmffBoxType.MovieExtension)]
    [InlineData(BmffBoxType.MovieFragment)]
    [InlineData(BmffBoxType.MovieFragmentRandomAccess)]
    [InlineData(BmffBoxType.PartitionEntry)]
    [InlineData(BmffBoxType.ProtectionSchemeInfo)]
    [InlineData(BmffBoxType.SampleTable)]
    [InlineData(BmffBoxType.SchemeInformation)]
    [InlineData(BmffBoxType.Track)]
    [InlineData(BmffBoxType.TrackFragment)]
    [InlineData(BmffBoxType.TrackReference)]
    [InlineData(BmffBoxType.UserData)]
    public void Load_PureContainer_MaterializesChildTree(BmffBoxType type)
    {
        // Arrange
        var bytes = BmffFixtures.Box(type.ToFourCC().ToString(), BmffFixtures.Box("zzzz", new byte[] { 42 }));
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        var container = document.Boxes[0].ShouldBeAssignableTo<BmffBoxComposite>();
        container.Children.Count.ShouldBe(1);
        container.Children[0].ShouldBeOfType<UnknownBox>();
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Invalid UUID sizes do not read adjacent bytes")]
    public void TryRead_InvalidUuidSize_StopsBeforeExtendedType()
    {
        // Arrange
        var bytes = BmffFixtures.Box("uuid");
        bytes[3] = 8;
        using var memory = new MemoryStream(bytes);
        using var limited = new ReadLimitStream(memory, 8);
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffBoxHeader.TryRead(limited, 0, bytes.Length, true, out _));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        memory.Position.ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Strict mode rejects unknown types")]
    public void Read_UnknownInStrictMode_ReportsUnsupported()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("zzzz"));
        using var reader = BmffReader.Create(source, new BmffReaderOptions { Strict = true });
        // Act
        var error = Should.Throw<ContentFormatException>(() => reader.Read());
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        error.Position.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Raw high-bit FourCC values remain lossless")]
    public void ToFourCC_UnregisteredHighBitType_RoundTrips()
    {
        // Arrange
        var fourcc = FourCC.FromUInt32(0xff008001);
        // Act
        var type = fourcc.ToBmffBoxType();
        // Assert
        type.ToFourCC().ShouldBe(fourcc);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Registered full-box versions preserve opaque bytes")]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Load_ProfileUnsupportedPrefix_PreservesBytes(byte version, byte flags)
    {
        // Arrange
        var bytes = BmffFixtures.Box("test", new byte[] { version, 0, 0, flags });
        var registry = BmffBoxRegistry.CreateBuilder().Register(FourCC.Parse("test").ToBmffBoxType(), header => new ProfileBox(header)).Build();
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source, registry: registry);
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        document.Boxes[0].ShouldBeOfType<UnknownBox>();
        output.ToArray().ShouldBe(bytes);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Known boxes retain large-size encodings")]
    [InlineData("mdat")]
    [InlineData("free")]
    [InlineData("skip")]
    [InlineData("ftyp")]
    public void Save_KnownLargeHeader_PreservesEncoding(string type)
    {
        // Arrange
        var bytes = BmffFixtures.Box(type, new byte[8], large: true);
        using var source = new MemoryStream(bytes);
        // Act
        using var document = BmffDocument.Load(source);
        using var output = new MemoryStream();
        document.Save(output);
        // Assert
        output.ToArray().ShouldBe(bytes);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Profile composite overrides retain per-box writing")]
    public void Save_ProfileComposite_UsesItsSizeAndWriteOverrides()
    {
        // Arrange
        var bytes = BmffFixtures.Box("cont", BmffFixtures.Join(new byte[] { 42 }, BmffFixtures.Box("free")));
        var registry = BmffBoxRegistry.CreateBuilder().Register(FourCC.Parse("cont").ToBmffBoxType(), header => new ProfileContainer(header)).Build();
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source, registry: registry);
        using var output = new MemoryStream();
        // Act
        document.Save(output);
        // Assert
        output.ToArray().ShouldBe(bytes);
        ((ProfileContainer)document.Boxes[0]).WriteCalls.ShouldBe(1);
    }
}
