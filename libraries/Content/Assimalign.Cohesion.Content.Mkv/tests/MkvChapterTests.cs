using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

public sealed class MkvChapterTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§4.2,5.1.7: Typed chapter reads preserve the existing golden byte for byte")]
    public void Load_TypedGoldenChapter_PreservesOriginalBytes()
    {
        // Arrange — existing writer-generated, license-clean corpus.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestObjects", "Golden", "metadata.mkv"));
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);
        var edition = document.Chapters[0];
        var chapter = edition.Chapters[0];

        // Act
        chapter.Displays[0].Text.ShouldBe("Introduction");
        chapter.TimeStart.ShouldBe(0UL);
        chapter.Uid.ShouldBe(1UL);
        chapter.TimeEnd.ShouldBeNull();
        chapter.StringUid.ShouldBeNull();
        chapter.IsEnabled.ShouldBeTrue();
        chapter.IsHidden.ShouldBeFalse();
        chapter.Displays[0].Languages.ShouldBeEmpty(); // Implicit eng; no wire node synthesized.
        edition.Uid.ShouldBeNull();
        edition.IsDefault.ShouldBeFalse();
        edition.IsOrdered.ShouldBeFalse();
        using var output = new MemoryStream();
        document.Save(output);

        // Assert
        output.ToArray().ShouldBe(bytes);
        MkvSchema.Matroska.Find(VInt.FromEncoded(0x80))!.Type.ShouldBe(EbmlElementType.MasterElement);
        MkvSchema.Matroska.Find(VInt.FromEncoded(0xB6))!.Recursive.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§5.1.7,6.4: Adding, renaming and retiming chapters preserves frames and cue positions")]
    public async Task Save_TypedChapterEdits_RoundTripsAllFields()
    {
        // Arrange — writer-produced source with cues, clusters and an existing chapter.
        using var source = new MemoryStream(MkvFixtures.Create(webm: false, metadata: true));
        using var document = MkvDocument.Load(source);
        var edition = document.Chapters[0];
        edition.Uid = 42;
        edition.IsDefault = true;
        edition.IsOrdered = true;
        var first = edition.Chapters[0];
        first.Displays[0].Text = new string('x', 300) + " café";
        first.TimeStart = 123;
        first.TimeEnd = 1_000_000_000;
        first.StringUid = "intro";
        first.IsHidden = true;
        first.IsEnabled = false;
        first.Displays[0].Languages.Add("fra");
        first.Displays[0].Languages.Add("eng");
        first.Displays[0].LanguagesBCP47.Add("fr-CA");
        first.Displays[0].Countries.Add("ca");
        var second = MkvChapter.Create(2, 1_000_000_001);
        second.TimeEnd = 2_000_000_000;
        second.Displays.Add(MkvChapterDisplay.Create("Second"));
        var nested = MkvChapter.Create(3, 1_500_000_000);
        nested.TimeEnd = 1_750_000_000;
        nested.Displays.Add(MkvChapterDisplay.Create("Nested"));
        second.Chapters.Add(nested);
        edition.Chapters.Add(second);
        var originalSample = await document.Tracks[0].Samples.GetSampleAsync(2, CancellationToken.None);
        var originalBytes = BinarySegment.Create(source, originalSample!.Value.ByteOffset, originalSample.Value.ByteLength).ToArray(128);

        // Act
        using var output = new MemoryStream();
        using (var forward = new ForwardWriteStream(output)) { document.Save(forward); }
        output.Position = 0;
        using var loaded = MkvDocument.Load(output);
        var actual = loaded.Chapters[0].Chapters[0];
        var seek = await loaded.Tracks[0].Samples.SeekAsync(new MediaTimestamp(1750, 1000), CancellationToken.None);

        // Assert
        loaded.Chapters[0].Uid.ShouldBe(42UL);
        loaded.Chapters[0].IsDefault.ShouldBeTrue();
        loaded.Chapters[0].IsOrdered.ShouldBeTrue();
        loaded.Chapters[0].Chapters.Count.ShouldBe(2);
        actual.Displays[0].Text.ShouldBe(first.Displays[0].Text);
        actual.TimeStart.ShouldBe(123UL);
        actual.TimeEnd.ShouldBe(1_000_000_000UL);
        actual.StringUid.ShouldBe("intro");
        actual.IsHidden.ShouldBeTrue();
        actual.IsEnabled.ShouldBeFalse();
        actual.Displays[0].Languages.ShouldBe(new[] { "fra", "eng" });
        actual.Displays[0].LanguagesBCP47.ShouldBe(new[] { "fr-CA" });
        actual.Displays[0].Countries.ShouldBe(new[] { "ca" });
        loaded.Chapters[0].Chapters[1].Chapters[0].Displays[0].Text.ShouldBe("Nested");
        seek!.Value.SyncSample.Index.ShouldBe(2);
        BinarySegment.Create(output, seek.Value.SyncSample.ByteOffset, seek.Value.SyncSample.ByteLength).ToArray(128).ShouldBe(originalBytes);
        using var unchanged = new MemoryStream();
        loaded.Save(unchanged);
        unchanged.ToArray().ShouldBe(output.ToArray());
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.7: Empty chapter views add and remove the optional container")]
    public void Chapters_InitiallyAbsent_SupportsListEdits()
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Create(webm: false, cues: false));
        using var document = MkvDocument.Load(source);
        document.Chapters.ShouldBeEmpty();
        Should.Throw<ArgumentOutOfRangeException>(() => document.Chapters[0]);
        var edition = new MkvChapterEdition();
        var first = MkvChapter.Create(10, 0);
        var second = MkvChapter.Create(20, 1);
        edition.Chapters.Add(first);
        edition.Chapters.Insert(0, second);
        document.Chapters.Add(edition);

        // Act
        document.Chapters.Contains(document.Chapters[0]).ShouldBeTrue();
        edition.Chapters.IndexOf(first).ShouldBe(1);
        edition.Chapters.Remove(edition.Chapters[0]).ShouldBeTrue();
        var display = MkvChapterDisplay.Create("A");
        first.Displays.Add(display);
        display.Languages.Add("eng");
        display.Languages.Insert(0, "fra");
        display.Languages[1] = "deu";
        display.Languages.Remove("fra").ShouldBeTrue();
        var copied = new MkvChapter[1];
        edition.Chapters.CopyTo(copied, 0);
        copied[0].Uid.ShouldBe(10UL);
        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;
        using var reloaded = MkvDocument.Load(output);
        reloaded.Chapters[0].Chapters[0].Displays[0].Languages[0].ShouldBe("deu");
        document.Chapters.Clear();

        // Assert
        using var cleared = new MemoryStream();
        document.Save(cleared);
        cleared.ToArray().ShouldBe(source.ToArray());
        document.Chapters.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.7: Native and typed chapter edits remain live and preserve opaque extensions")]
    public void Save_TypedAndNativeEdits_RetainsUnknownBytes()
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Create(webm: false, metadata: true, cues: false));
        using var document = MkvDocument.Load(source);
        var atom = MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1043A770).Children[0], 0xB6);
        atom.Children.Add(EbmlElement.Create(VInt.FromEncoded(0x6E67), EbmlElementType.Binary, BinarySegment.Create(new byte[] { 1, 2, 3 })));
        MkvFixtures.Find(atom, 0x91).Value = 9UL;
        document.Chapters[0].Chapters[0].TimeStart.ShouldBe(9UL);
        document.Chapters[0].Chapters[0].Displays[0].Text = "Renamed";

        // Act
        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;
        using var loaded = MkvDocument.Load(output);

        // Assert
        loaded.Chapters[0].Chapters[0].TimeStart.ShouldBe(9UL);
        var savedAtom = MkvFixtures.Find(MkvFixtures.Find(loaded.Segment, 0x1043A770).Children[0], 0xB6);
        ((BinarySegment)MkvFixtures.Find(savedAtom, 0x6E67).Value!).ToArray(3).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.7: Invalid chapter fields fail before output")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Save_InvalidChapter_ReportsClassifiedFailure(int scenario)
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Create(webm: false, metadata: true));
        using var document = MkvDocument.Load(source);
        var edition = document.Chapters[0];
        var chapter = edition.Chapters[0];
        if (scenario == 0) { chapter.TimeStart = 10; chapter.TimeEnd = 9; }
        if (scenario == 1) { edition.Chapters.Clear(); }
        if (scenario == 2) { edition.IsOrdered = true; }
        if (scenario == 3)
        {
            var atom = MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1043A770).Children[0], 0xB6);
            MkvFixtures.Find(atom, 0x73C4).Value = 0UL;
        }
        using var output = new MemoryStream();

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => document.Save(output));
        error.ErrorCode.ShouldBe(scenario is 0 or 3 ? ContentFormatErrorCode.InvalidValue : ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldNotBeNull();
        output.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.7: Recursive chapter depth remains bounded")]
    public void Load_DeepChapterTree_EnforcesDepthLimit()
    {
        // Arrange — valid tree generated by our own writer, then read with a smaller limit.
        using var source = new MemoryStream(MkvFixtures.Create(webm: false, cues: false));
        using var document = MkvDocument.Load(source);
        var edition = new MkvChapterEdition();
        var chapter = MkvChapter.Create(1, 0);
        edition.Chapters.Add(chapter);
        document.Chapters.Add(edition);
        for (ulong i = 2; i < 12; i++)
        {
            var child = MkvChapter.Create(i, i);
            chapter.Chapters.Add(child);
            chapter = child;
        }
        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => MkvDocument.Load(output,
            new MkvReaderOptions { Ebml = new EbmlReaderOptions { MaxDepth = 8 } }));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
}
