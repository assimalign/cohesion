using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Versioning.Tests;

public class OpenApiPointerCompatibilityTests
{
    [Theory(DisplayName = "Cohesion Test [OpenApi.Versioning] - Pointers: Should retain escaping and nested item locations")]
    [InlineData(1)]
    [InlineData(4)]
    public void Transform_NestedItems_ShouldPreservePointer(int depth)
    {
        // Arrange
        OpenApiSchema schema = new() { Type = SchemaType.String, Format = "binary" };
        for (int i = 0; i < depth; i++)
        {
            schema = new OpenApiSchema { Type = SchemaType.Array, Items = schema };
        }
        OpenApiDocument document = new()
        {
            SpecVersion = OpenApiSpecVersion.V3_0,
            Info = new OpenApiInfo { Title = "Pointers", Version = "1" },
            Components = new OpenApiComponents()
        };
        document.Components.Schemas["root/~"] = schema;

        // Act
        OpenApiTransformResult result = OpenApiVersionTransformer.Transform(document, OpenApiSpecVersion.V3_1);

        // Assert
        result.Diagnostics.Single(item => item.Code == OpenApiTransformDiagnosticCodes.BinaryFormatConverted)
            .Location.ShouldBe("#/components/schemas/root~1~0" + string.Concat(Enumerable.Repeat("/items", depth)));
        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_0);
    }
}
