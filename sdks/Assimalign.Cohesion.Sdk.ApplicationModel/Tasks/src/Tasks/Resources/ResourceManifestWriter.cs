using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

internal static class ResourceManifestWriter
{
    public static void Write(string path, ResourceManifestModel manifest)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("schema", "cohesion/resource/v1");
        writer.WriteString("name", manifest.Name);
        writer.WriteString("kind", manifest.Kind);
        writer.WriteString("application", manifest.Application);
        writer.WriteString("applicationModel", manifest.ApplicationModel);

        writer.WriteStartObject("artifact");
        writer.WriteString("assembly", manifest.Artifact.Assembly);
        writer.WriteBoolean("composable", manifest.Artifact.Composable);
        writer.WriteString("project", manifest.Artifact.Project);
        writer.WriteString("apphost", manifest.Artifact.AppHost);
        writer.WriteNull("image");
        writer.WriteEndObject();

        writer.WriteStartArray("endpoints");
        foreach (ResourceEndpointModel endpoint in manifest.Endpoints)
        {
            writer.WriteStartObject();
            writer.WriteString("name", endpoint.Name);
            writer.WriteString("scheme", endpoint.Scheme);
            writer.WriteString("protocol", endpoint.Protocol);
            writer.WriteNumber("containerPort", endpoint.ContainerPort);
            if (endpoint.DevPort is int devPort)
            {
                writer.WriteNumber("devPort", devPort);
            }
            writer.WriteBoolean("public", endpoint.Public);
            if (!string.IsNullOrWhiteSpace(endpoint.Certificate))
            {
                writer.WriteString("certificate", endpoint.Certificate);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartObject("probes");
        foreach ((string role, ResourceProbeModel probe) in manifest.Probes)
        {
            writer.WriteStartObject(role);
            if (!string.IsNullOrWhiteSpace(probe.Endpoint))
            {
                writer.WriteString("endpoint", probe.Endpoint);
            }
            if (probe.Http is not null)
            {
                writer.WriteString("http", probe.Http);
            }
            else if (probe.Tcp is bool tcp)
            {
                writer.WriteBoolean("tcp", tcp);
            }
            else if (probe.Exec is not null)
            {
                writer.WriteStartArray("exec");
                foreach (string argument in probe.Exec)
                {
                    writer.WriteStringValue(argument);
                }
                writer.WriteEndArray();
            }
            else if (probe.Grpc is not null)
            {
                writer.WriteString("grpc", probe.Grpc);
            }
            else if (probe.None is bool none)
            {
                writer.WriteBoolean("none", none);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        writer.WriteStartObject("controlPlane");
        writer.WriteString("endpoint", manifest.ControlPlane.Endpoint);
        writer.WriteString("path", manifest.ControlPlane.Path);
        writer.WriteEndObject();

        writer.WriteStartArray("mounts");
        foreach (ResourceMountModel mount in manifest.Mounts)
        {
            writer.WriteStartObject();
            writer.WriteString("name", mount.Name);
            writer.WriteString("kind", mount.Kind);
            writer.WriteString("containerPath", mount.ContainerPath);
            if (mount.Source is not null)
            {
                writer.WriteString("source", mount.Source);
            }
            if (mount.Size is not null)
            {
                writer.WriteString("size", mount.Size);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("settings");
        foreach (ResourceSettingModel setting in manifest.Settings)
        {
            writer.WriteStartObject();
            writer.WriteString("key", setting.Key);
            if (setting.Default is not null)
            {
                writer.WriteString("default", setting.Default);
            }
            if (!string.Equals(setting.Type, "string", System.StringComparison.OrdinalIgnoreCase))
            {
                writer.WriteString("type", setting.Type);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("references");
        foreach (ResourceReferenceModel reference in manifest.References)
        {
            writer.WriteStartObject();
            writer.WriteString("resource", reference.Resource);
            writer.WriteString("application", reference.Application);
            writer.WriteStartArray("endpoints");
            foreach (ResourceEndpointModel endpoint in reference.Endpoints)
            {
                writer.WriteStringValue(endpoint.Name);
            }
            writer.WriteEndArray();
            writer.WriteBoolean("optional", reference.Optional);
            writer.WriteString("manifest", reference.Manifest);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("commands");
        foreach (string command in manifest.Commands)
        {
            writer.WriteStringValue(command);
        }
        writer.WriteEndArray();

        // Keep empty environment metadata explicit: ResourceManifest treats every collection
        // member as required after deserialization, and an omitted object becomes null rather
        // than the record property's initializer under source-generated System.Text.Json.
        writer.WriteStartObject("environment");
        writer.WriteEndObject();

        writer.WriteStartObject("lifecycle");
        writer.WriteString("workload", manifest.Lifecycle.Workload);
        writer.WriteNumber("replicas", manifest.Lifecycle.Replicas);
        if (manifest.Lifecycle.MaxReplicas is int maxReplicas)
        {
            writer.WriteNumber("maxReplicas", maxReplicas);
        }
        else
        {
            writer.WriteNull("maxReplicas");
        }
        writer.WriteNumber("stopGraceSeconds", manifest.Lifecycle.StopGraceSeconds);
        writer.WriteString("restartPolicy", manifest.Lifecycle.RestartPolicy);
        writer.WriteString("exitCodes", "cohesion/sysexits/v1");
        writer.WriteEndObject();

        writer.WriteStartObject("properties");
        foreach ((string key, string value) in manifest.Properties)
        {
            writer.WriteString(key, value);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.Flush();
        ResourceFileWriter.WriteIfChanged(
            path,
            new System.ReadOnlySpan<byte>(stream.GetBuffer(), 0, checked((int)stream.Length)));
    }
}
