using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Fluent.Tests;

public class OpenApiFluentAdvancedSurfaceTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Advanced: callbacks, webhooks, links, and externalDocs author cleanly")]
    public void Build_AdvancedSurfaces_MapToModel()
    {
        // Shapes drawn from the official OpenAPI callback-example, link-example, and webhook-example
        // documents (learn.openapis.org/examples).
        var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "Advanced", "1.0.0")
            .ExternalDocs("https://example.com/docs", "Find more info here")
            .Path("/subscribe", path => path
                .Operation(OperationType.Post, op => op
                    .OperationId("subscribe")
                    .ExternalDocs("https://example.com/ops/subscribe")
                    .Callback("onData", cb => cb
                        .Expression("{$request.body#/callbackUrl}", item => item
                            .Operation(OperationType.Post, callbackOp => callbackOp
                                .Response("200", r => r.Description("callback processed")))))
                    .Response("201", r => r.Description("subscribed")
                        .Link("self", link => link.OperationId("getSubscription").Parameter("id", "$response.body#/id")))))
            .Webhook("newPet", hook => hook
                .Operation(OperationType.Post, op => op
                    .RequestBody(b => b.Content("application/json", m => m.SchemaReference("#/components/schemas/Pet")))
                    .Response("200", r => r.Description("ack"))))
            .Components(c => c
                .Schema("Pet", s => s.Type(SchemaType.Object).ExternalDocs("https://example.com/pet"))
                .Link("GetSub", link => link.OperationId("getSubscription")))
            .Tag("pets", t => t.Description("Pet ops").ExternalDocs("https://example.com/tags/pets"))
            .Build();

        // Callbacks
        var operation = document.Paths!.Items["/subscribe"].Operations[OperationType.Post];
        operation.ExternalDocs!.Url.ShouldBe("https://example.com/ops/subscribe");
        operation.Callbacks["onData"].PathItems["{$request.body#/callbackUrl}"].Operations.ShouldContainKey(OperationType.Post);

        // Links
        operation.Responses!.Items["201"].Links["self"].OperationId.ShouldBe("getSubscription");
        operation.Responses.Items["201"].Links["self"].Parameters["id"].ShouldNotBeNull();
        document.Components!.Links["GetSub"].OperationId.ShouldBe("getSubscription");

        // Webhooks
        document.Webhooks["newPet"].Operations[OperationType.Post].Responses!.Items["200"].Description.ShouldBe("ack");

        // ExternalDocs across surfaces
        document.ExternalDocs!.Url.ShouldBe("https://example.com/docs");
        document.Components.Schemas["Pet"].ExternalDocs!.Url.ShouldBe("https://example.com/pet");
        document.Tags[0].ExternalDocs!.Url.ShouldBe("https://example.com/tags/pets");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Advanced: an advanced-surface document validates and round-trips")]
    public void Build_AdvancedSurfaces_ValidatesAndRoundTrips()
    {
        var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "Advanced", "1.0.0")
            .Path("/subscribe", path => path
                .Operation(OperationType.Post, op => op
                    .Callback("onData", cb => cb
                        .Expression("{$request.body#/callbackUrl}", item => item
                            .Operation(OperationType.Post, callbackOp => callbackOp
                                .Response("200", r => r.Description("ok")))))
                    .Response("201", r => r.Description("subscribed")
                        .Link("self", link => link.OperationRef("#/paths/~1subscribe/post")))))
            .Webhook("newPet", hook => hook
                .Operation(OperationType.Post, op => op.Response("200", r => r.Description("ack"))))
            .Build();

        document.Validate().IsValid.ShouldBeTrue();

        var json = document.ToJson(indented: false);
        OpenApiJson.Parse(json).ToJson(indented: false).ShouldBe(json);

        var yaml = document.ToYaml();
        OpenApiYaml.Parse(yaml).ToYaml().ShouldBe(yaml);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Advanced: externalDocs on a tag is authorable in every line")]
    public void Build_TagExternalDocs_AllVersions()
    {
        foreach (var version in new[] { OpenApiSpecVersion.V3_0, OpenApiSpecVersion.V3_1, OpenApiSpecVersion.V3_2 })
        {
            var document = OpenApiDocumentBuilder.Create(version, "t", "1.0.0")
                .Tag("pets", t => t.Description("d").ExternalDocs("https://example.com"))
                .Build();

            document.Tags[0].ExternalDocs!.Url.ShouldBe("https://example.com");
        }
    }
}
