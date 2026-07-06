using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Serialization.Tests;

/// <summary>
/// Regression coverage for the advanced description surfaces (callbacks, webhooks, links, tags,
/// externalDocs), using documents shaped after the official OpenAPI examples
/// (learn.openapis.org/examples: callback-example, link-example, webhook-example).
/// </summary>
public class OpenApiAdvancedSurfaceRoundTripTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Advanced: a callback document round-trips")]
    public void RoundTrip_Callback()
    {
        const string json = """
            {"openapi":"3.1.2","info":{"title":"Callback Example","version":"1.0.0"},"paths":{"/streams":{"post":{"operationId":"createStream","callbacks":{"onData":{"{$request.query.callbackUrl}/data":{"post":{"requestBody":{"description":"payload","content":{"application/json":{"schema":{"type":"object"}}}},"responses":{"200":{"description":"ok"}}}}}},"responses":{"201":{"description":"created"}}}}}}
            """;

        AssertRoundTrips(json);

        var document = OpenApiJson.Parse(json);
        var callback = document.Paths!.Items["/streams"].Operations[OperationType.Post].Callbacks["onData"];
        callback.PathItems["{$request.query.callbackUrl}/data"].Operations.ShouldContainKey(OperationType.Post);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Advanced: a link document round-trips")]
    public void RoundTrip_Link()
    {
        const string json = """
            {"openapi":"3.1.2","info":{"title":"Link Example","version":"1.0.0"},"paths":{"/users/{id}":{"get":{"operationId":"getUser","parameters":[{"name":"id","in":"path","required":true,"schema":{"type":"string"}}],"responses":{"200":{"description":"user","links":{"repositories":{"operationId":"getRepos","parameters":{"username":"$response.body#/login"}}}}}}}},"components":{"links":{"UserRepos":{"operationRef":"#/paths/~1users~1{id}/get"}}}}
            """;

        AssertRoundTrips(json);

        var document = OpenApiJson.Parse(json);
        var link = document.Paths!.Items["/users/{id}"].Operations[OperationType.Get].Responses!.Items["200"].Links["repositories"];
        link.OperationId.ShouldBe("getRepos");
        link.Parameters["username"].ShouldNotBeNull();
        document.Components!.Links["UserRepos"].OperationRef.ShouldBe("#/paths/~1users~1{id}/get");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Advanced: a webhook document round-trips")]
    public void RoundTrip_Webhook()
    {
        const string json = """
            {"openapi":"3.1.2","info":{"title":"Webhook Example","version":"1.0.0"},"webhooks":{"newPet":{"post":{"requestBody":{"description":"a new pet","content":{"application/json":{"schema":{"type":"object"}}}},"responses":{"200":{"description":"ok"}}}}}}
            """;

        AssertRoundTrips(json);

        var document = OpenApiJson.Parse(json);
        document.Webhooks["newPet"].Operations[OperationType.Post].Responses!.Items["200"].Description.ShouldBe("ok");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Advanced: tags with externalDocs and 3.2 hierarchy round-trip")]
    public void RoundTrip_TagsWithExternalDocsAndHierarchy()
    {
        const string json = """
            {"openapi":"3.2.0","info":{"title":"Tags","version":"1.0.0"},"paths":{},"tags":[{"name":"account-updates","summary":"Account Updates","parent":"pets","kind":"nav","description":"desc","externalDocs":{"description":"more","url":"https://example.com/tags"}}]}
            """;

        AssertRoundTrips(json);

        var document = OpenApiJson.Parse(json);
        var tag = document.Tags[0];
        tag.Parent.ShouldBe("pets");
        tag.Kind.ShouldBe("nav");
        tag.ExternalDocs!.Url.ShouldBe("https://example.com/tags");
    }

    private static void AssertRoundTrips(string json)
    {
        var document = OpenApiJson.Parse(json);
        var emitted = document.ToJson(document.SpecVersion, indented: false);
        OpenApiJson.Parse(emitted).ToJson(document.SpecVersion, indented: false).ShouldBe(emitted);
    }
}
