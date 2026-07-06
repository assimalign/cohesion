using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Fluent.Tests;

public class OpenApiFluentBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Build: a representative document maps to the canonical model")]
    public void Build_RepresentativeDocument_MatchesModel()
    {
        var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "Petstore", "1.0.0")
            .Info(i => i.Summary("A sample API").Description("Longer description").License("MIT"))
            .Server("https://api.example.com", "production")
            .Path("/pets/{id}", path => path
                .Operation(OperationType.Get, op => op
                    .OperationId("getPet")
                    .Summary("Get a pet")
                    .Tag("pets")
                    .Parameter("id", ParameterLocation.Path, p => p.Required().Schema(s => s.Type(SchemaType.Integer)))
                    .Response("200", r => r.Description("A pet")
                        .Content("application/json", m => m.SchemaReference("#/components/schemas/Pet")))))
            .Components(c => c
                .Schema("Pet", s => s
                    .Type(SchemaType.Object)
                    .Property("id", p => p.Type(SchemaType.Integer))
                    .Property("name", p => p.Type(SchemaType.String))
                    .Required("id", "name")))
            .Tag("pets", t => t.Description("Pet operations"))
            .Build();

        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);
        document.Info.Title.ShouldBe("Petstore");
        document.Info.Version.ShouldBe("1.0.0");
        document.Info.Summary.ShouldBe("A sample API");
        document.Info.License!.Name.ShouldBe("MIT");
        document.Servers[0].Url.ShouldBe("https://api.example.com");

        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.OperationId.ShouldBe("getPet");
        operation.Parameters[0].In.ShouldBe(ParameterLocation.Path);
        operation.Parameters[0].Required.ShouldBeTrue();
        operation.Responses!.Items["200"].Content["application/json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/Pet");

        var pet = document.Components!.Schemas["Pet"];
        pet.Type.ShouldBe(SchemaType.Object);
        pet.Required.ShouldBe(["id", "name"]);
        document.Tags[0].Name.ShouldBe("pets");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Build: a fluent document passes default validation")]
    public void Build_FluentDocument_IsValid()
    {
        var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "Petstore", "1.0.0")
            .Path("/pets", path => path
                .Operation(OperationType.Get, op => op
                    .OperationId("listPets")
                    .Response("200", r => r.Description("Pets"))))
            .Build();

        var result = document.Validate();

        result.IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Build: path parameters default to required")]
    public void Build_PathParameter_DefaultsRequired()
    {
        var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "t", "1")
            .Path("/x/{id}", path => path.Operation(OperationType.Get, op => op
                .Parameter("id", ParameterLocation.Path, p => p.Schema(s => s.Type(SchemaType.String)))
                .Response("200", r => r.Description("ok"))))
            .Build();

        document.Paths!.Items["/x/{id}"].Operations[OperationType.Get].Parameters[0].Required.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Build: security schemes and requirements compose")]
    public void Build_Security_Composes()
    {
        var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "t", "1")
            .Components(c => c.SecurityScheme("api_key", s => s.ApiKey("X-Key", ParameterLocation.Header)))
            .Security("api_key")
            .Path("/x", path => path.Operation(OperationType.Get, op => op
                .Security("api_key")
                .Response("200", r => r.Description("ok"))))
            .Build();

        document.Components!.SecuritySchemes["api_key"].Type.ShouldBe(SecuritySchemeType.ApiKey);
        document.Security[0].Schemes.ShouldContainKey("api_key");
        document.Validate().IsValid.ShouldBeTrue();
    }
}
