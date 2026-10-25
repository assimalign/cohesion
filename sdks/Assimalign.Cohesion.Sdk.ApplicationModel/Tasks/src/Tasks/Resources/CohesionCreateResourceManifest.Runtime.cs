using System.Buffers;
using System.Text.Json;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

public sealed partial class CohesionCreateResourceManifest
{
    private JsonElement CreateRuntimeBinding()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "cohesion/resource-runtime/v1");
            writer.WriteStartObject("management");
            writer.WriteString("endpoint", ControlPlaneEndpoint);
            writer.WriteString("path", ControlPlanePath);
            writer.WriteString("protocol", "cohesion/resource-state/v1");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
