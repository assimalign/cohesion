using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfMutationTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.6: Nested resolved dictionary and array edits are tracked")]
    public void Resolve_NestedMutation_MarksOwningObjectChanged()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic());
        using var document = new PdfReader().Read(source);
        var id = PdfObjectIdentifier.Create(2);
        var pages = document.Resolve(id).ShouldBeOfType<PdfDictionary>();
        document.IsChanged(id).ShouldBeFalse();
        // Act
        pages[PdfName.Create("Resources"u8)].ShouldBeOfType<PdfDictionary>()[PdfName.Create("Example"u8)] = PdfBoolean.Create(true);
        // Assert
        document.IsChanged(id).ShouldBeTrue();
        document.HasEdits.ShouldBeTrue();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.10 and §7.5.4: Allocate replace and free preserve identity rules")]
    public void Mutate_Objects_AllocatesAndInvalidatesFreedIdentity()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic());
        using var document = new PdfReader().Read(source);
        // Act
        var id = document.Add(PdfString.Create("first"u8));
        document.Replace(id, PdfInteger.Create(42));
        document.Resolve(id).ShouldBeOfType<PdfInteger>().Value.ShouldBe(42);
        document.Free(id);
        // Assert
        id.Number.ShouldBe(4);
        document.Resolve(id).ShouldBeOfType<PdfNull>();
        document.CrossReferences[4].Identifier.Generation.ShouldBe(1);
        document.CrossReferences[4].Kind.ShouldBe(PdfCrossReferenceKind.Free);
        document.Add(new PdfNull()).Number.ShouldBe(5);
        Should.Throw<ArgumentException>(() => document.Replace(id, new PdfNull()));
        Should.Throw<ArgumentException>(() => document.Free(default));
        Should.Throw<ArgumentNullException>(() => document.Add(null!));
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8 and §7.5.5: Stream replacement and trailer edits are retained")]
    public void ReplaceData_NewBytes_UpdatesImmediateViewAndTracksChanges()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.IndirectLength());
        using var document = new PdfReader().Read(source);
        var id = PdfObjectIdentifier.Create(4);
        var stream = document.Resolve(id).ShouldBeOfType<PdfStream>();
        // Act
        stream.ReplaceData(BinarySegment.Create("replacement"u8.ToArray()));
        document.Trailer[PdfName.Create("Info"u8)] = PdfIndirectReference.Create(document.Add(new PdfDictionary()));
        // Assert
        stream.Decode().ToArray().ShouldBe("replacement"u8.ToArray());
        stream.Dictionary.Get("Filter").ShouldBeNull();
        document.IsChanged(id).ShouldBeTrue();
        document.HasEdits.ShouldBeTrue();
    }
}
