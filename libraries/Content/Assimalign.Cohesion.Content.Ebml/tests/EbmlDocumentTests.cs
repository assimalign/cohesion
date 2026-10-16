using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlDocumentTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Save: recalculates CRC after metadata edits")]
    public void Save_EditedCrcParent_RecalculatesChecksum()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        document.Body.Elements[0].Children.Insert(0, EbmlElement.Create(VInt.FromEncoded(0xBF), EbmlElementType.Binary,
            BinarySegment.Create(new byte[4])));
        using var original = new MemoryStream();
        document.Save(original);
        original.Position = 0;
        using var loaded = EbmlDocument.Load(original, document.Schema, new EbmlReaderOptions { VerifyCrc32 = true });
        loaded.Body.Elements[0].Children[1].Value = 128UL;

        // Act
        using var saved = new MemoryStream();
        loaded.Save(saved);
        saved.Position = 0;
        using var reloaded = EbmlDocument.Load(saved, document.Schema, new EbmlReaderOptions { VerifyCrc32 = true });

        // Assert
        reloaded.Body.Elements[0].Children[1].Value.ShouldBe(128UL);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Content contracts: use async I/O including CRC and lazy copy-through")]
    public async Task ReadAsync_AsyncOnlyStreams_RoundTripsWithoutSyncIo()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        document.Body.Elements[0].Children.Insert(0, EbmlElement.Create(VInt.FromEncoded(0xBF), EbmlElementType.Binary,
            BinarySegment.Create(new byte[4])));
        using var original = new MemoryStream();
        using var source = new AsyncOnlyStream(original);
        var writer = EbmlDocument.CreateWriter();
        var reader = EbmlDocument.CreateReader(document.Schema, new EbmlReaderOptions { VerifyCrc32 = true });

        // Act
        await writer.WriteAsync(source, document, CancellationToken.None);
        source.Position = 0;
        using var loaded = await reader.ReadAsync(source, CancellationToken.None);
        using var copied = new MemoryStream();
        using var destination = new AsyncOnlyStream(copied);
        await writer.WriteAsync(destination, loaded, CancellationToken.None);

        // Assert
        copied.ToArray().ShouldBe(original.ToArray());
        ((BinarySegment)loaded.Body.Elements[0].Children[^1].Value!).IsMaterialized.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - ReadAsync: cancellation during short reads leaves source borrowed")]
    public async Task ReadAsync_CancelledDuringIo_StopsParsing()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        using var original = new MemoryStream();
        document.Save(original);
        original.Position = 0;
        using var cancellation = new CancellationTokenSource();
        using var source = new AsyncOnlyStream(original, cancellation, 10);

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() =>
            EbmlDocument.CreateReader(document.Schema).ReadAsync(source, cancellation.Token).AsTask());
        source.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Save: constructed document round-trips byte-for-byte")]
    public void Save_ConstructedDocument_PreservesBytesAndLazyUnknowns()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        using var original = new MemoryStream();
        document.Save(original);
        original.Position = 0;

        // Act
        using var loaded = EbmlDocument.Load(original, document.Schema);
        using var saved = new MemoryStream();
        loaded.Save(saved);

        // Assert
        saved.ToArray().ShouldBe(original.ToArray());
        loaded.Header.DocType.ShouldBe("cohesion-test");
        var unknown = loaded.Body.Elements[0].Children[^1];
        ((BinarySegment)unknown.Value!).IsMaterialized.ShouldBeFalse();
        unknown.SizeLength.ShouldBe(3);
        loaded.Dispose();
        original.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Save: edited metadata recomputes parent lengths")]
    public void Save_EditedMetadata_UpdatesSizesAndCopiesBinary()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        using var original = new MemoryStream();
        document.Save(original);
        original.Position = 0;
        using var loaded = EbmlDocument.Load(original, document.Schema);
        loaded.Body.Elements[0].Children[3].Value = new string('x', 200);

        // Act
        using var saved = new MemoryStream();
        loaded.Save(saved);
        saved.Position = 0;
        using var reloaded = EbmlDocument.Load(saved, document.Schema);

        // Assert
        reloaded.Body.Elements[0].Children[3].Value.ShouldBe(new string('x', 200));
        ((BinarySegment)reloaded.Body.Elements[0].Children[^1].Value!).ToArray(3).ShouldBe(new byte[] { 9, 8, 7 });
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Dispose: closes only explicitly owned sources")]
    public void Dispose_OwnedSource_ClosesSource()
    {
        // Arrange
        using var built = EbmlTestData.Document();
        var source = new MemoryStream();
        built.Save(source);
        source.Position = 0;
        var loaded = EbmlDocument.Load(source, built.Schema, leaveOpen: false);

        // Act
        loaded.Dispose();

        // Assert
        source.CanRead.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Content contracts: load and save with cancellation")]
    public async Task ReadAsync_ContentContracts_RoundTrips()
    {
        // Arrange
        using var built = EbmlTestData.Document();
        using var stream = new MemoryStream();
        var writer = EbmlDocument.CreateWriter();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await writer.WriteAsync(stream, built, cancellation.Token);
        stream.Position = 0;
        using var loaded = await EbmlDocument.CreateReader(built.Schema).ReadAsync(stream, cancellation.Token);

        // Assert
        loaded.Body.Elements.Count.ShouldBe(1);
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => writer.WriteAsync(stream, loaded, cancellation.Token).AsTask());
    }
}
