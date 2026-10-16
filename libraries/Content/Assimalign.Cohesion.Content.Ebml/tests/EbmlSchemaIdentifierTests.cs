using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlSchemaIdentifierTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §5: ReadNext: explicitly defined zero-data ID is accepted")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadNext_DefinedZeroDataIdentifier_Accepts(bool lenient)
    {
        // Arrange — bytes emitted by the EBML writer; 0x80 is a schema-defined master.
        var schema = CreateSchema(true);
        using var bytes = new MemoryStream();
        using (var writer = EbmlWriter.Create(bytes, schema))
        {
            writer.BeginMaster(VInt.FromEncoded(0x80), 3);
            writer.WriteUnsignedInteger(VInt.FromEncoded(0x81), 7);
            writer.EndMaster();
        }
        bytes.Position = 0;
        using var reader = EbmlReader.Create(bytes, schema, new EbmlReaderOptions { Lenient = lenient });

        // Act
        reader.ReadNext().ShouldBeTrue();
        reader.Current.Identifier.EncodedValue.ShouldBe(0x80UL);
        reader.EnterMaster();
        reader.ReadNext().ShouldBeTrue();
        reader.ReadUnsignedInteger().ShouldBe(7UL);
        reader.ReadNext().ShouldBeFalse();
        reader.LeaveMaster();

        // Assert
        reader.ReadNext().ShouldBeFalse();
        reader.Diagnostics.ShouldBeEmpty();
        VInt.FromEncoded(0x80).IsValidIdentifier.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §5: ReadNext: undefined zero-data ID is fatal in both modes")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadNext_UndefinedZeroDataIdentifier_Rejects(bool lenient)
    {
        // Arrange — hand-built forbidden identifier fixture.
        using var bytes = new MemoryStream(new byte[] { 0x80, 0x80 });
        using var reader = EbmlReader.Create(bytes, options: new EbmlReaderOptions { Lenient = lenient });

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => reader.ReadNext());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§5,11: Validate: schema controls model ID eligibility in both modes")]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_ZeroDataIdentifier_RequiresDefinition(bool lenient)
    {
        // Arrange
        using var document = EbmlDocument.Create(CreateSchema(true));
        document.Header.DocType = "schema-id";
        document.Body.Elements.Add(EbmlElement.Create(VInt.FromEncoded(0x80), EbmlElementType.MasterElement));

        // Act / Assert
        document.Schema.Validate(document, lenient).ShouldBeEmpty();
        Should.Throw<ContentFormatException>(() => CreateSchema(false).Validate(document, lenient))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        using var bytes = new MemoryStream();
        using var writer = EbmlWriter.Create(bytes);
        Should.Throw<ArgumentException>(() => writer.WriteElement(document.Body.Elements[0]));
        bytes.Length.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§5,11: ReadAsync: DocType-selected zero-data ID round trips")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_RegisteredZeroDataIdentifier_RoundTrips(bool lenient)
    {
        // Arrange
        var schema = CreateSchema(true);
        using var document = EbmlDocument.Create(schema);
        document.Header.DocType = "schema-id";
        document.Body.Elements.Add(EbmlElement.Create(VInt.FromEncoded(0x80), EbmlElementType.MasterElement));
        using var bytes = new MemoryStream();
        await EbmlDocument.CreateWriter().WriteAsync(bytes, document, CancellationToken.None);
        var registry = EbmlSchemaRegistry.Create(new Dictionary<string, EbmlSchema> { ["schema-id"] = schema });
        bytes.Position = 0;

        // Act
        using var loaded = await EbmlDocument.CreateReader(options: new EbmlReaderOptions { Lenient = lenient },
            registry: registry).ReadAsync(bytes, CancellationToken.None);
        using var saved = new MemoryStream();
        await EbmlDocument.CreateWriter().WriteAsync(saved, loaded, CancellationToken.None);

        // Assert
        saved.ToArray().ShouldBe(bytes.ToArray());
        bytes.Position = 0;
        var error = await Should.ThrowAsync<ContentFormatException>(() =>
            EbmlDocument.CreateReader(options: new EbmlReaderOptions { Lenient = lenient })
                .ReadAsync(bytes, CancellationToken.None).AsTask());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §4: Create: uninitialized IDs remain invalid")]
    public void Create_DefaultIdentifier_Rejects()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => EbmlElement.Create(default, EbmlElementType.MasterElement));
        Should.Throw<ArgumentException>(() => EbmlElementDefinition.Create(default, "Invalid", EbmlElementType.MasterElement));
        Should.Throw<ArgumentException>(() => EbmlElementDefinition.Create(VInt.FromEncoded(0x81),
            "InvalidParent", EbmlElementType.Binary, [default(VInt)]));
    }

    private static EbmlSchema CreateSchema(bool includeZero)
    {
        var builder = new EbmlSchemaBuilder().Include(EbmlSchema.Header);
        if (includeZero)
        {
            builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x80), "DefinedMaster", EbmlElementType.MasterElement));
            builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x81), "Child", EbmlElementType.UnsignedInteger,
                [VInt.FromEncoded(0x80)]));
        }
        return builder.Build();
    }
}
