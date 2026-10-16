using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

public class Mp4SampleEntryDispatchTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - stsd registry dispatch: ISO 14496-12 8.5.2 strict and lenient")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadChildren_RegisteredAndUnknownEntries_PreservesBoth(bool strict)
    {
        // Arrange: original sample entry and configuration emitted by our writer.
        var entry = new Avc1SampleEntryBox { Width = 32, Height = 24 };
        entry.Children.Add(new AvcConfigurationBox { Data = BinarySegment.Create(new byte[] { 1, 2 }) });
        var bytes = Mp4Fixtures.Write(Mp4Fixtures.Container(new SampleDescriptionBox(0, 0), entry, Mp4Fixtures.Opaque("zzzz", [9])));
        using var source = new MemoryStream(bytes);
        using var reader = BmffReader.Create(source, new BmffReaderOptions { Strict = strict }, Mp4Profile.CreateRegistry());

        // Act
        reader.Read().ShouldBeTrue();
        using var entries = reader.ReadChildren();
        entries.Read().ShouldBeTrue();
        var loaded = entries.Current.ShouldBeOfType<Avc1SampleEntryBox>();

        // Assert
        loaded.Width.ShouldBe((ushort)32);
        loaded.Children[0].ShouldBeOfType<AvcConfigurationBox>().Data.Length.ShouldBe(2);
        entries.Read().ShouldBeTrue();
        entries.Current.ShouldBeOfType<UnknownBox>();
        entries.Read().ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - stsd nested entry depth: ISO 14496-12 8.5.2")]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Load_EntryChildren_UsesEnclosingDepthBudget(int depth, bool succeeds)
    {
        // Arrange
        var entry = new Avc1SampleEntryBox();
        entry.Children.Add(new AvcConfigurationBox());
        var bytes = Mp4Fixtures.Write(Mp4Fixtures.Container(new SampleDescriptionBox(0, 0), entry));
        using var source = new MemoryStream(bytes);

        // Act / Assert
        if (succeeds)
        {
            using var document = Mp4Profile.LoadDocument(source, new BmffReaderOptions { MaxDepth = depth });
            ((SampleDescriptionBox)document.Boxes[0]).Children[0].ShouldBeOfType<Avc1SampleEntryBox>();
        }
        else
        {
            var error = Should.Throw<ContentFormatException>(() => Mp4Profile.LoadDocument(source, new BmffReaderOptions { MaxDepth = depth }));
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
            error.Position.ShouldBe(102);
        }
    }
}
