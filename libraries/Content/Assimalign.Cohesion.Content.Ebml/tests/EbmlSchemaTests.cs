using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlSchemaTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - Defaults: zero remains distinct from a nonzero schema default")]
    [InlineData(0UL, 1)]
    [InlineData(7UL, 0)]
    public void WriteUnsignedInteger_DefaultedValue_UsesCorrectMinimalLength(ulong value, int length)
    {
        // Arrange
        var schema = new EbmlSchemaBuilder().Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x81), "Number",
            EbmlElementType.UnsignedInteger, defaultValue: 7UL)).Build();
        using var bytes = new MemoryStream();
        using var writer = EbmlWriter.Create(bytes, schema);

        // Act
        writer.WriteUnsignedInteger(VInt.FromEncoded(0x81), value);
        bytes.Position = 0;
        using var reader = EbmlReader.Create(bytes, schema);
        reader.ReadNext().ShouldBeTrue();

        // Assert
        reader.Current.DataSize.ShouldBe(length);
        reader.ReadValue().ShouldBe(value);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Build: schema snapshots are immutable")]
    public void Build_AfterBuilderChanges_PreservesSnapshot()
    {
        // Arrange
        var builder = new EbmlSchemaBuilder().Include(EbmlSchema.Header);
        var original = builder.Build();

        // Act
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xC0), "Extra", EbmlElementType.Binary));

        // Assert
        original.Find(VInt.FromEncoded(0xC0)).ShouldBeNull();
        builder.Build().Find(VInt.FromEncoded(0xC0)).ShouldNotBeNull();
        Should.Throw<ArgumentException>(() => builder.Include(EbmlSchema.Header));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Registry: derived DocType resolves typed body without core edits")]
    public void Load_RegisteredDerivedType_ResolvesBody()
    {
        // Arrange
        var schema = DerivedSchema.Create();
        using var document = EbmlDocument.Create(schema);
        document.Header.DocType = "derived-test";
        var root = EbmlElement.Create(VInt.FromEncoded(0xC0), EbmlElementType.MasterElement);
        root.Children.Add(EbmlElement.Create(VInt.FromEncoded(0xC1), EbmlElementType.UnsignedInteger, 5UL));
        document.Body.Elements.Add(root);
        using var bytes = new MemoryStream();
        document.Save(bytes);
        bytes.Position = 0;
        var registry = EbmlSchemaRegistry.Create(new Dictionary<string, EbmlSchema> { ["derived-test"] = schema });

        // Act
        using var loaded = EbmlDocument.Load(bytes, registry: registry);

        // Assert
        loaded.Schema.ShouldBeSameAs(schema);
        loaded.Body.Elements[0].Children[0].Value.ShouldBe(5UL);
        schema.Find(VInt.FromEncoded(0xC2))!.DefaultValue.ShouldBe(7UL);
        registry.Find("DERIVED-TEST").ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - Validation: range violations throw or record diagnostics")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadValue_OutOfRange_UsesValidationMode(bool lenient)
    {
        // Arrange — deliberately malformed fixture built directly in code.
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0xC0, 0x83, 0xC1, 0x81, 11 }),
            DerivedSchema.Create(), new EbmlReaderOptions { Lenient = lenient });
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        if (lenient)
        {
            reader.ReadValue().ShouldBe(11UL);
            reader.Diagnostics.Count.ShouldBe(1);
            reader.Diagnostics[0].ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
            reader.Diagnostics[0].Position.ShouldBe(4);
        }
        else
        {
            var error = Should.Throw<ContentFormatException>(() => reader.ReadValue());
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
            error.Position.ShouldBe(4);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Validation: missing required child is detected at master exit")]
    public void LeaveMaster_MissingMandatoryChild_ReportsStructure()
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0xC0, 0x80 }), DerivedSchema.Create());
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();
        reader.ReadNext().ShouldBeFalse();

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => reader.LeaveMaster());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Validation: empty values resolve declared defaults")]
    public void ReadValue_EmptyDefaultedElement_UsesSchemaDefault()
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0xC0, 0x85, 0xC1, 0x81, 5, 0xC2, 0x80 }), DerivedSchema.Create());
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();
        reader.Skip();
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        reader.ReadUnsignedInteger().ShouldBe(7UL);
        reader.ReadNext().ShouldBeFalse();
        reader.LeaveMaster();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Validation: wrong model type has a classified failure")]
    public void Validate_WrongTypeForId_ReportsInvalidValue()
    {
        // Arrange
        using var document = EbmlDocument.Create(DerivedSchema.Create());
        var root = EbmlElement.Create(VInt.FromEncoded(0xC0), EbmlElementType.MasterElement);
        root.Children.Add(EbmlElement.Create(VInt.FromEncoded(0xC1), EbmlElementType.Utf8String, "wrong"));
        document.Body.Elements.Add(root);

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => document.Schema.Validate(document));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - Validation: invalid scalar sizes retain raw bytes in lenient mode")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadValue_InvalidScalarWidth_ReportsOrPreserves(bool lenient)
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0xA0, 0x85, 0xA3, 0x83, 1, 2, 3 }),
            EbmlTestData.Schema(), new EbmlReaderOptions { Lenient = lenient });
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();

        // Act / Assert
        if (lenient)
        {
            reader.ReadNext().ShouldBeTrue();
            ((BinarySegment)reader.ReadValue()).ToArray(3).ShouldBe(new byte[] { 1, 2, 3 });
            reader.Diagnostics[0].ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        }
        else
        {
            Should.Throw<ContentFormatException>(() => reader.ReadNext()).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Validation: duplicate singleton is rejected")]
    public void ReadNext_DuplicateSingleton_ReportsStructure()
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0xC0, 0x86, 0xC1, 0x81, 1, 0xC1, 0x81, 2 }), DerivedSchema.Create());
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();
        reader.Skip();

        // Act / Assert
        Should.Throw<ContentFormatException>(() => reader.ReadNext()).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Validation: unknown-size eligibility is schema controlled")]
    public void ReadNext_DisallowedUnknownSize_ReportsStructure()
    {
        // Arrange
        var schema = new EbmlSchemaBuilder().Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xC0), "KnownOnly", EbmlElementType.MasterElement)).Build();
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0xC0, 0xFF }), schema);

        // Act / Assert
        Should.Throw<ContentFormatException>(() => reader.ReadNext()).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - Build: invalid parent and recursive unknown-size definitions are refused")]
    public void Build_InvalidDefinitions_RejectsConfiguration()
    {
        // Arrange
        var builder = new EbmlSchemaBuilder().Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xC1), "Orphan",
            EbmlElementType.Binary, [VInt.FromEncoded(0xC0)]));

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.Build());
        Should.Throw<ArgumentException>(() => EbmlElementDefinition.Create(VInt.FromEncoded(0xC0), "Recursive",
            EbmlElementType.MasterElement, unknownSizeAllowed: true, recursive: true));
        Should.Throw<ArgumentException>(() => EbmlElementDefinition.Create(VInt.FromEncoded(0xC0), "WrongDefault",
            EbmlElementType.UnsignedInteger, defaultValue: "wrong"));
    }
}
