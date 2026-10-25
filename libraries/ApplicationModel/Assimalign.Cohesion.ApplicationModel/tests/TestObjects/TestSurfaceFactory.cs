using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>Builds runtime surfaces shaped like the schema exporter's output for typical options types.</summary>
internal static class TestSurfaceFactory
{
    // Web:Cors is a live process section with a nested object, an array and a dictionary; Orders:Limits is a
    // restart section with a required member and an enumeration; Orders:Schema is a claimable backend section.
    public const string Document = """
        {"schema":"cohesion/runtime/v1","configuration":[
        {"path":"Web:Cors","mutability":"live","scope":"process","claimable":false,"schema":{"type":["object","null"],"properties":{"defaultPolicy":{"type":["object","null"],"properties":{"origins":{"type":["array","null"],"items":{"type":"string"}},"maxAgeSeconds":{"type":"integer"}}},"policies":{"type":["object","null"],"additionalProperties":{"$ref":"#/properties/defaultPolicy"}}}}},
        {"path":"Orders:Limits","mutability":"restart","scope":"process","claimable":false,"schema":{"type":["object","null"],"properties":{"maxItems":{"type":"integer"},"mode":{"enum":["fast","safe"]},"ratio":{"type":"number"}},"required":["maxItems"]}},
        {"path":"Orders:Schema","mutability":"deployment","scope":"backend","claimable":true,"schema":{"type":["object","null"],"additionalProperties":{"type":["object","null"],"properties":{"columns":{"type":"integer"}}}}}],
        "actions":[{"name":"flush-cache","target":"everyInstance","input":{"type":["object","null"],"properties":{"region":{"type":["string","null"]}}},"result":{"type":["object","null"],"properties":{"flushed":{"type":"integer"}}}}],
        "tools":[{"name":"sql","endpoint":"db","protocol":"websocket","subprotocol":"cohesion.sql","requiresDataAccess":true}]}
        """;

    public static ResourceSurface Create() => ResourceSurface.Parse(Document);

    public static ResourceManifest CreateManifest(string name = "api") =>
        TestManifestFactory.Create(name) with { Surface = Create() };
}
