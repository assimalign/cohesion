using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Generation.Tests;

/// <summary>
/// The pass-through schema members: a producer that already holds a complete schema (the Web OpenAPI
/// adapter) hands it to generation, which places it as it is instead of building one from the flat fields.
/// </summary>
public class OpenApiSchemaPassThroughTests
{
    private static OpenApiSchema OrderComponent()
    {
        var schema = new OpenApiSchema { Type = SchemaType.Object };
        schema.Properties["id"] = new OpenApiSchema { Type = SchemaType.Integer, Format = "int64" };
        schema.Properties["lines"] = new OpenApiSchema
        {
            Type = SchemaType.Array,
            Items = new OpenApiSchema { Type = SchemaType.String }
        };
        schema.Required.Add("id");
        return schema;
    }

    private static OpenApiSchema OrderArray() => new()
    {
        Type = SchemaType.Array,
        Items = new OpenApiSchema { Reference = new OpenApiReference { Ref = "#/components/schemas/Order" } }
    };

    private static OpenApiGenerationInput Input(OpenApiSchema component, OpenApiSchema body, OpenApiSchema response, OpenApiSchema parameter) => new()
    {
        Operations =
        [
            new OpenApiOperationMetadata
            {
                Method = OperationType.Post,
                Path = "/orders/{tenant}",
                OperationId = "createOrders",
                Parameters =
                [
                    new OpenApiParameterMetadata { Name = "tenant", In = ParameterLocation.Path, Required = true, SchemaType = SchemaType.Integer, Schema = parameter }
                ],
                RequestBody = new OpenApiRequestBodyMetadata { ContentType = "application/json", Required = true, SchemaReference = "#/components/schemas/Ignored", Schema = body },
                Responses =
                [
                    new OpenApiResponseMetadata { StatusCode = "200", Description = "OK", ContentType = "application/json", SchemaReference = "#/components/schemas/Ignored", Schema = response }
                ]
            }
        ],
        Schemas =
        [
            new OpenApiSchemaMetadata { Name = "Order", Type = SchemaType.String, Schema = component }
        ]
    };

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: supplied schemas are placed as they are")]
    public void Generate_SuppliedSchemas_ShouldPlaceThemAsTheyAre()
    {
        // Arrange
        OpenApiSchema component = OrderComponent();
        OpenApiSchema body = OrderArray();
        OpenApiSchema response = OrderArray();
        OpenApiSchema parameter = new() { Type = SchemaType.String, Format = "uuid" };

        // Act
        var document = OpenApiDocumentGenerator.Generate(
            Input(component, body, response, parameter),
            new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "Orders", ApiVersion = "1.0.0" });

        // Assert — the supplied schemas win over the flat type, format and reference fields.
        var operation = document.Paths!.Items["/orders/{tenant}"].Operations[OperationType.Post];
        operation.Parameters[0].Schema.ShouldBeSameAs(parameter);
        operation.RequestBody!.Content["application/json"].Schema.ShouldBeSameAs(body);
        operation.Responses!.Items["200"].Content["application/json"].Schema.ShouldBeSameAs(response);
        document.Components!.Schemas["Order"].ShouldBeSameAs(component);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: a document built from supplied schemas validates on every line")]
    [InlineData(OpenApiSpecVersion.V3_0)]
    [InlineData(OpenApiSpecVersion.V3_1)]
    [InlineData(OpenApiSpecVersion.V3_2)]
    public void Generate_SuppliedSchemas_ShouldValidate(OpenApiSpecVersion version)
    {
        // Arrange
        var input = Input(OrderComponent(), OrderArray(), OrderArray(), new OpenApiSchema { Type = SchemaType.String });

        // Act
        var document = OpenApiDocumentGenerator.Generate(input, new OpenApiGenerationOptions { Version = version, Title = "Orders", ApiVersion = "1.0.0" });

        // Assert
        document.Validate().IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: without a supplied schema the flat fields still build one")]
    public void Generate_NoSuppliedSchema_ShouldBuildFromFlatFields()
    {
        // Arrange
        var input = new OpenApiGenerationInput
        {
            Operations =
            [
                new OpenApiOperationMetadata
                {
                    Method = OperationType.Get,
                    Path = "/orders/{id}",
                    Parameters = [new OpenApiParameterMetadata { Name = "id", In = ParameterLocation.Path, Required = true, SchemaType = SchemaType.Integer, Format = "int64" }],
                    Responses = [new OpenApiResponseMetadata { StatusCode = "200", Description = "OK", ContentType = "application/json", SchemaReference = "#/components/schemas/Order" }]
                }
            ],
            Schemas = [new OpenApiSchemaMetadata { Name = "Order", Type = SchemaType.Object }]
        };

        // Act
        var document = OpenApiDocumentGenerator.Generate(input, new OpenApiGenerationOptions { Title = "Orders", ApiVersion = "1.0.0" });

        // Assert
        var operation = document.Paths!.Items["/orders/{id}"].Operations[OperationType.Get];
        operation.Parameters[0].Schema!.Format.ShouldBe("int64");
        operation.Responses!.Items["200"].Content["application/json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/Order");
        document.Components!.Schemas["Order"].Type.ShouldBe(SchemaType.Object);
    }
}
