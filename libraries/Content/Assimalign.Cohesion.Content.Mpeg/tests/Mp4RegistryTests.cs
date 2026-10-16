using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

public sealed class Mp4RegistryTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Opaque sample-entry child records (ISO/IEC 14496-12 8.5.2; ISO/IEC 14496-14 5)")]
    [InlineData("avcC")][InlineData("hvcC")][InlineData("av1C")][InlineData("vpcC")]
    [InlineData("pasp")][InlineData("colr")][InlineData("btrt")][InlineData("esds")]
    [InlineData("dOps")][InlineData("dac3")][InlineData("dec3")]
    public void Read_RetainedOpaqueRecord_ShouldPreservePayload(string code)
    {
        // Arrange
        var bytes = Mp4Fixtures.Write(Mp4Fixtures.Opaque(code, [0, 0, 0, 0]));
        using var source = new MemoryStream(bytes);
        using var reader = BmffReader.Create(source, null, Mp4Profile.CreateRegistry());
        // Act
        reader.Read().ShouldBeTrue();
        var box = reader.Current.ShouldBeAssignableTo<BmffBoxComposite>();
        // Assert
        box.IsLeaf.ShouldBeTrue();
        box.IsComposite.ShouldBeFalse();
        box.Children.ShouldBeEmpty();
        Mp4Fixtures.Write(box).ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Registry isolation (ISO/IEC 14496-12 8.5.2)")]
    public void CreateRegistry_ProfileEntry_ShouldLeaveDefaultUntouched()
    {
        // Arrange
        var bytes = Mp4Fixtures.Write(new Avc1SampleEntryBox { Width = 1920, Height = 1080, CompressorName = "fixture" });
        using var defaultSource = new MemoryStream(bytes);
        using var profileSource = new MemoryStream(bytes);
        using var ordinary = BmffReader.Create(defaultSource);
        using var profile = BmffReader.Create(profileSource, options: null, registry: Mp4Profile.CreateRegistry());

        // Act
        ordinary.Read().ShouldBeTrue();
        profile.Read().ShouldBeTrue();

        // Assert
        ordinary.Current.ShouldBeOfType<UnknownBox>();
        var entry = profile.Current.ShouldBeOfType<Avc1SampleEntryBox>();
        entry.Width.ShouldBe((ushort)1920);
        entry.Height.ShouldBe((ushort)1080);
        entry.CompressorName.ShouldBe("fixture");
        Mp4Fixtures.Write(entry).ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Sample-entry extension and lazy config (ISO/IEC 14496-12 8.5.2)")]
    public void LoadDocument_SampleDescription_ShouldBindAndPreserveLazyConfiguration()
    {
        // Arrange
        var entry = new Avc1SampleEntryBox { Width = 16, Height = 16 };
        entry.Children.Add(new AvcConfigurationBox { Data = BinarySegment.Create(new byte[] { 1, 2, 3 }) });
        var bytes = Mp4Fixtures.Write(Mp4Fixtures.Container(new SampleDescriptionBox(0, 0), entry, Mp4Fixtures.Opaque("zzzz", [9])));
        using var source = new MemoryStream(bytes);

        // Act
        using var document = Mp4Profile.LoadDocument(source);
        var description = document.Boxes[0].ShouldBeOfType<SampleDescriptionBox>();
        var loaded = description.Children[0].ShouldBeOfType<Avc1SampleEntryBox>();
        var configuration = loaded.Children[0].ShouldBeOfType<AvcConfigurationBox>();

        // Assert
        configuration.Data.IsMaterialized.ShouldBeFalse();
        description.Children[1].ShouldBeOfType<UnknownBox>();
        using var copy = new MemoryStream();
        document.Save(copy);
        copy.ToArray().ShouldBe(bytes);
    }

    public static IEnumerable<object[]> Entries()
    {
        yield return [new Avc1SampleEntryBox()];
        yield return [new Avc3SampleEntryBox()];
        yield return [new Hvc1SampleEntryBox()];
        yield return [new Hev1SampleEntryBox()];
        yield return [new Av01SampleEntryBox()];
        yield return [new Vp09SampleEntryBox()];
        yield return [new Mp4aSampleEntryBox()];
        yield return [new OpusSampleEntryBox()];
        yield return [new Ac3SampleEntryBox()];
        yield return [new Ec3SampleEntryBox()];
        yield return [new WvttSampleEntryBox()];
        yield return [new StppSampleEntryBox()];
        yield return [new Tx3gSampleEntryBox()];
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Retained sample prefixes (ISO/IEC 14496-12 8.5.2)")]
    [MemberData(nameof(Entries))]
    public void LoadDocument_RetainedEntry_ShouldRoundTrip(BmffBoxComposite entry)
    {
        // Arrange
        var bytes = Mp4Fixtures.Write(Mp4Fixtures.Container(new SampleDescriptionBox(0, 0), entry));
        using var source = new MemoryStream(bytes);

        // Act
        using var document = Mp4Profile.LoadDocument(source);
        using var output = new MemoryStream();
        document.Save(output);

        // Assert
        ((SampleDescriptionBox)document.Boxes[0]).Children[0].ShouldBeAssignableTo<BmffBoxComposite>();
        output.ToArray().ShouldBe(bytes);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Retained brand declarations (ISO/IEC 14496-12 8.1.2; ISO/IEC 14496-14 4)")]
    [InlineData("isom")][InlineData("iso2")][InlineData("iso3")][InlineData("iso4")][InlineData("iso5")]
    [InlineData("iso6")][InlineData("iso7")][InlineData("iso8")][InlineData("iso9")]
    [InlineData("mp41")][InlineData("mp42")][InlineData("avc1")][InlineData("M4A ")][InlineData("M4V ")]
    [InlineData("dash")][InlineData("msdh")][InlineData("msix")][InlineData("cmfc")][InlineData("cmf2")][InlineData("cmfs")][InlineData("cmfl")]
    public void InterpretBrand_RetainedCode_ShouldRecognizeDeclaration(string code)
    {
        // Arrange
        var brand = FourCC.Parse(code);
        // Act
        var features = Mp4Profile.InterpretBrand(brand);
        // Assert
        features.ShouldNotBe(Mp4BrandFeatures.None);
        Mp4Profile.InterpretBrand(FourCC.Parse("zzzz")).ShouldBe(Mp4BrandFeatures.None);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Package direction (ISO/IEC 14496-14 4)")]
    public void References_ProfilePackage_ShouldRemainAboveBmff()
    {
        // Arrange
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Assimalign.Cohesion.slnx")))
        {
            directory = directory.Parent;
        }
        var root = directory.ShouldNotBeNull().FullName;
        // Act
        var bmff = File.ReadAllText(Path.Combine(root, "libraries/Content/Assimalign.Cohesion.Content.Bmff/src/Assimalign.Cohesion.Content.Bmff.csproj"));
        var mpeg = File.ReadAllText(Path.Combine(root, "libraries/Content/Assimalign.Cohesion.Content.Mpeg/src/Assimalign.Cohesion.Content.Mpeg.csproj"));
        // Assert
        bmff.ShouldNotContain("Content.Mpeg", Case.Sensitive);
        mpeg.ShouldContain("Content.Bmff", Case.Sensitive);
        mpeg.ShouldContain("Content.Media", Case.Sensitive);
    }
}
