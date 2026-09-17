using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal static class LocalPlanSetWriter
{
    private const string Schema = "cohesion/local-plan-set/v1";

    public static async Task WriteAsync(
        ResourceName gateway,
        string processKind,
        string endpointAllocation,
        IReadOnlyList<IApplicationModel> models,
        Func<IApplicationResource, LocalRenderArtifact> resolveArtifact,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointAllocation);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(resolveArtifact);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("gateway", gateway.ToString());
            writer.WriteStartArray("applications");

            for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IApplicationModel model = models[modelIndex]
                    ?? throw new ArgumentException(
                        $"Application model at index {modelIndex} is null.",
                        nameof(models));
                if (model.Descriptors.Count != model.Plans.Count)
                {
                    throw new InvalidOperationException(
                        $"Application '{model.Name}' cannot be rendered because its descriptor and plan counts differ.");
                }

                writer.WriteStartObject();
                writer.WriteString("application", model.Name.ToString());
                writer.WriteString("owner", model.Owner);
                writer.WriteStartArray("resources");

                for (int resourceIndex = 0; resourceIndex < model.Descriptors.Count; resourceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IApplicationResourceDescriptor descriptor = model.Descriptors[resourceIndex];
                    ResourcePlan plan = model.Plans[resourceIndex];
                    writer.WriteStartObject();
                    writer.WriteString("name", descriptor.Resource.Name.ToString());
                    WriteDependencies(writer, descriptor.Dependencies);

                    if (IsExternal(plan))
                    {
                        WriteExternal(writer, plan);
                    }
                    else
                    {
                        LocalRenderArtifact artifact = resolveArtifact(descriptor.Resource);
                        WriteProcess(
                            writer,
                            gateway,
                            processKind,
                            endpointAllocation,
                            plan,
                            artifact);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        string document = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await output.WriteLineAsync(document.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static void WriteDependencies(
        Utf8JsonWriter writer,
        IReadOnlyList<IApplicationResourceDescriptor> dependencies)
    {
        writer.WriteStartArray("dependencies");
        for (int index = 0; index < dependencies.Count; index++)
        {
            writer.WriteStringValue(dependencies[index].Resource.Name.ToString());
        }

        writer.WriteEndArray();
    }

    private static void WriteProcess(
        Utf8JsonWriter writer,
        ResourceName gateway,
        string processKind,
        string endpointAllocation,
        ResourcePlan plan,
        LocalRenderArtifact artifact)
    {
        writer.WriteStartObject("unit");
        writer.WriteString("kind", processKind);
        writer.WriteStartObject("artifact");
        writer.WriteString("identity", artifact.Identity);
        if (artifact.ContentRoot is not null)
        {
            writer.WriteString("contentRoot", artifact.ContentRoot);
        }

        writer.WriteEndObject();
        writer.WriteString("workloadKind", plan.Workload.Kind.ToString());
        writer.WriteNumber("replicas", plan.Workload.Replicas);
        writer.WriteBoolean("stableIdentity", plan.Workload.StableIdentity);
        writer.WriteString("restartPolicy", plan.Workload.RestartPolicy);
        writer.WriteNumber("stopGraceSeconds", plan.Workload.StopGraceSeconds);
        WriteGate(writer, plan.Workload.Gate);
        WriteEndpoints(writer, plan.Container.Ports, plan.Exposures, endpointAllocation);
        WriteMounts(writer, plan.Container.Mounts, plan.Volumes, processKind);
        WriteEnvironment(writer, gateway, artifact.ContentRoot, plan.Container.Environment);
        WriteProbes(writer, plan.Container.Probes);
        writer.WriteStartObject("controlPlane");
        writer.WriteString("endpoint", plan.ControlPlane.Endpoint);
        writer.WriteString("path", plan.ControlPlane.Path);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteGate(Utf8JsonWriter writer, ReadinessGate gate)
    {
        writer.WriteStartObject("gate");
        WriteLifecycleArray(writer, "terminals", gate.Terminals);
        WriteLifecycleArray(writer, "satisfying", gate.Satisfying);
        writer.WriteEndObject();
    }

    private static void WriteLifecycleArray(
        Utf8JsonWriter writer,
        string name,
        IReadOnlyList<ResourceLifecycle> states)
    {
        writer.WriteStartArray(name);
        for (int index = 0; index < states.Count; index++)
        {
            writer.WriteStringValue(states[index].ToString());
        }

        writer.WriteEndArray();
    }

    private static void WriteEndpoints(
        Utf8JsonWriter writer,
        IReadOnlyList<PortBinding> ports,
        IReadOnlyList<ExposureSpec> exposures,
        string endpointAllocation)
    {
        writer.WriteStartArray("endpoints");
        for (int index = 0; index < ports.Count; index++)
        {
            PortBinding port = ports[index];
            writer.WriteStartObject();
            writer.WriteString("name", port.Endpoint);
            writer.WriteString("scheme", port.Scheme);
            writer.WriteString("protocol", port.Protocol);
            writer.WriteNumber("containerPort", port.ContainerPort);
            writer.WriteString("host", "127.0.0.1");
            writer.WriteNumber("port", 0);
            writer.WriteBoolean("public", IsPublicEndpoint(exposures, port.Endpoint));
            writer.WriteString("allocation", endpointAllocation);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMounts(
        Utf8JsonWriter writer,
        IReadOnlyList<MountBinding> mounts,
        IReadOnlyList<VolumeSpec> volumes,
        string processKind)
    {
        writer.WriteStartArray("mounts");
        for (int index = 0; index < mounts.Count; index++)
        {
            MountBinding mount = mounts[index];
            writer.WriteStartObject();
            writer.WriteString("name", mount.Mount);
            writer.WriteString("kind", mount.Kind.ToString());
            writer.WriteString("targetPath", mount.ContainerPath);
            writer.WriteString("source", mount.Source);
            string? size = FindVolumeSize(volumes, mount.Mount);
            if (size is not null)
            {
                writer.WriteString("size", size);
            }

            writer.WriteString(
                "materialization",
                mount.Kind is ResourceMountKind.Volume
                    ? "persistentDirectory"
                    : string.Equals(processKind, "inProcessHost", StringComparison.Ordinal)
                        ? "ambientHandle"
                        : "file");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteEnvironment(
        Utf8JsonWriter writer,
        ResourceName gateway,
        string? contentRoot,
        IReadOnlyDictionary<string, string> declared)
    {
        var environment = new Dictionary<string, string>(declared, StringComparer.Ordinal)
        {
            [ResourceEnvironment.Gateway] = gateway.ToString(),
        };
        if (contentRoot is not null)
        {
            environment[ResourceEnvironment.ContentRoot] = contentRoot;
        }

        string[] names = new string[environment.Count];
        environment.Keys.CopyTo(names, 0);
        Array.Sort(names, StringComparer.Ordinal);

        writer.WriteStartObject("environment");
        for (int index = 0; index < names.Length; index++)
        {
            writer.WriteString(names[index], environment[names[index]]);
        }

        writer.WriteEndObject();
    }

    private static void WriteProbes(
        Utf8JsonWriter writer,
        IReadOnlyList<ProbeMapping> probes)
    {
        writer.WriteStartArray("probes");
        for (int index = 0; index < probes.Count; index++)
        {
            ProbeMapping probe = probes[index];
            writer.WriteStartObject();
            writer.WriteString("role", probe.Role);
            if (probe.Endpoint is null)
            {
                writer.WriteNull("endpoint");
            }
            else
            {
                writer.WriteString("endpoint", probe.Endpoint);
            }

            writer.WriteString("kind", probe.Kind.ToString());
            if (probe.Value is null)
            {
                writer.WriteNull("value");
            }
            else
            {
                writer.WriteString("value", probe.Value);
            }

            writer.WriteStartArray("command");
            for (int argumentIndex = 0; argumentIndex < probe.Command.Count; argumentIndex++)
            {
                writer.WriteStringValue(probe.Command[argumentIndex]);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static bool IsExternal(ResourcePlan plan) =>
        plan.Hints.TryGetValue(ExternalResourceController.PlanHint, out string? value)
        && string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicEndpoint(
        IReadOnlyList<ExposureSpec> exposures,
        string endpoint)
    {
        for (int index = 0; index < exposures.Count; index++)
        {
            if (string.Equals(exposures[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindVolumeSize(
        IReadOnlyList<VolumeSpec> volumes,
        string mount)
    {
        for (int index = 0; index < volumes.Count; index++)
        {
            if (string.Equals(volumes[index].Name, mount, StringComparison.Ordinal))
            {
                return volumes[index].Size;
            }
        }

        return null;
    }

    private static void WriteExternal(Utf8JsonWriter writer, ResourcePlan plan)
    {
        writer.WriteStartObject("external");
        WriteHint(writer, "application", plan, "cohesion.external.application");
        WriteHint(writer, "optional", plan, "cohesion.external.optional");
        WriteHint(writer, "endpoints", plan, "cohesion.external.endpoints");
        writer.WriteEndObject();
    }

    private static void WriteHint(
        Utf8JsonWriter writer,
        string propertyName,
        ResourcePlan plan,
        string hintName)
    {
        if (plan.Hints.TryGetValue(hintName, out string? value))
        {
            writer.WriteString(propertyName, value);
        }
    }
}

internal readonly record struct LocalRenderArtifact(
    string Identity,
    string? ContentRoot);
