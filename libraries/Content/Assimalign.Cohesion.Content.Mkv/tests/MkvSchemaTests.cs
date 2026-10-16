using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

public sealed class MkvSchemaTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §11: DocTypes register immutable schemas")]
    public void Registry_MatroskaAndWebM_ResolvesSchemas()
    {
        // Arrange / Act
        var registry = MkvSchema.Registry;

        // Assert
        registry.Find("matroska").ShouldBeSameAs(MkvSchema.Matroska);
        registry.Find("webm").ShouldBeSameAs(MkvSchema.WebM);
        registry.Find("WebM").ShouldBeNull();
        EbmlSchema.Header.Find(VInt.FromEncoded(0x18538067)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5: Retained hierarchy, defaults and limits")]
    public void Schema_RetainedElements_HasSpecificationConstraints()
    {
        // Arrange
        var schema = MkvSchema.Matroska;

        // Act / Assert
        schema.Find(VInt.FromEncoded(0x18538067))!.UnknownSizeAllowed.ShouldBeTrue();
        schema.Find(VInt.FromEncoded(0x1F43B675))!.UnknownSizeAllowed.ShouldBeTrue();
        schema.Find(VInt.FromEncoded(0x1549A966))!.UnknownSizeAllowed.ShouldBeFalse();
        schema.Find(VInt.FromEncoded(0x2AD7B1))!.DefaultValue.ShouldBe(1000000UL);
        schema.Find(VInt.FromEncoded(0xD7))!.MinimumValue.ShouldBe(1UL);
        schema.Find(VInt.FromEncoded(0x73A4))!.MaximumLength.ShouldBe(16);
        schema.Find(VInt.FromEncoded(0xBF))!.MaximumLength.ShouldBe(4);
        schema.Find(VInt.FromEncoded(0xEC))!.IsGlobal.ShouldBeTrue();
        schema.Find(VInt.FromEncoded(0x61A7))!.Type.ShouldBe(EbmlElementType.MasterElement);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §5.1.4: Invalid track number is classified")]
    public void Writer_ZeroTrackNumber_RejectsValue()
    {
        // Arrange
        using var output = new MemoryStream();
        using var writer = EbmlWriter.Create(output, MkvSchema.Matroska);

        // Act
        var error = Should.Throw<ContentFormatException>(() => writer.WriteUnsignedInteger(VInt.FromEncoded(0xD7), 0));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        error.Position.ShouldBe(0);
    }
}
