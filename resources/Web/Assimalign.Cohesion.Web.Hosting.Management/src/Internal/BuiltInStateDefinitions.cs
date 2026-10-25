using System;
using System.IO;
using System.Text.Json;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Management.Internal;

internal static class BuiltInStateDefinitions
{
    internal static ResourceStateDefinition Load(string name)
    {
        using Stream stream = typeof(BuiltInStateDefinitions).Assembly.GetManifestResourceStream("ResourceStates/" + name + ".json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement definition = document.RootElement;
        return ResourceStateDefinition.Create(name, new Uri(definition.GetProperty("payloadSchemaId").GetString()!),
            definition.GetProperty("keySchema"), definition.GetProperty("payloadSchema"), ResourceStateScope.EveryInstance,
            [ResourceStateUse.Orchestration, ResourceStateUse.Management]);
    }
}
