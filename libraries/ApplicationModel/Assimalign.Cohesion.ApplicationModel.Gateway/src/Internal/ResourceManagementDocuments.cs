using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>Writes the management API's JSON response documents, reflection-free.</summary>
internal static class ResourceManagementDocuments
{
    private static readonly JsonWriterOptions _indented = new() { Indented = true };

    // The surface and every layer with its provenance. Sections the surface declares come first, in its order;
    // a section only a layer names (a claim left from an older build, say) follows with "declared": false.
    public static byte[] WriteConfiguration(
        ApplicationName application,
        ResourceName resource,
        ResourceSurface? surface,
        ResourceRecord? record)
    {
        using JsonDocument? deployment = ParseLayer(record?.Deployment);
        using JsonDocument? overrides = ParseLayer(record?.Override);
        var claims = new List<(string Owner, JsonDocument Layer)>();
        try
        {
            if (record is not null)
            {
                foreach ((ApplicationName owner, ReadOnlyMemory<byte> layer) in record.Claims)
                {
                    claims.Add((owner.ToString(), JsonDocument.Parse(layer)));
                }

                claims.Sort(static (left, right) => string.CompareOrdinal(left.Owner, right.Owner));
            }

            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResourceSurfaceSection section in surface?.Configuration ?? Array.Empty<ResourceSurfaceSection>())
            {
                seen.Add(section.Path.Value);
                paths.Add(section.Path.Value);
            }

            var undeclared = new SortedSet<string>(StringComparer.Ordinal);
            AddUndeclared(deployment, seen, undeclared);
            AddUndeclared(overrides, seen, undeclared);
            foreach ((_, JsonDocument layer) in claims)
            {
                AddUndeclared(layer, seen, undeclared);
            }

            paths.AddRange(undeclared);
            return Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("application", application.ToString());
                writer.WriteString("resource", resource.ToString());
                WriteRecordIdentity(writer, record);
                writer.WritePropertyName("surface");
                if (surface is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    surface.WriteTo(writer);
                }

                writer.WriteStartArray("sections");
                foreach (string path in paths)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", path);
                    if (surface is not null && surface.TryGetSection(path, out ResourceSurfaceSection? declared))
                    {
                        writer.WriteBoolean("declared", true);
                        writer.WriteString("mutability", declared.Mutability switch
                        {
                            ResourceSurfaceMutability.Deployment => "deployment",
                            ResourceSurfaceMutability.Restart => "restart",
                            _ => "live",
                        });
                        writer.WriteString("scope", declared.Scope == ResourceSurfaceScope.Backend ? "backend" : "process");
                        writer.WriteBoolean("claimable", declared.Claimable);
                        writer.WriteBoolean("overridable", declared.IsOverridable);
                    }
                    else
                    {
                        writer.WriteBoolean("declared", false);
                    }

                    writer.WriteStartObject("layers");
                    WriteLayerMember(writer, "deployment", deployment, path);
                    bool claimed = false;
                    foreach ((string owner, JsonDocument layer) in claims)
                    {
                        if (layer.RootElement.TryGetProperty(path, out JsonElement patch))
                        {
                            if (!claimed)
                            {
                                writer.WriteStartObject("claims");
                                claimed = true;
                            }

                            writer.WritePropertyName(owner);
                            patch.WriteTo(writer);
                        }
                    }

                    if (claimed)
                    {
                        writer.WriteEndObject();
                    }

                    WriteLayerMember(writer, "override", overrides, path);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }
        finally
        {
            foreach ((_, JsonDocument layer) in claims)
            {
                layer.Dispose();
            }
        }
    }

    // The override a write left for its section ("overridden": false once it is removed), and the generation it
    // expires with.
    public static byte[] WriteOverride(ResourceSectionPath section, bool overridden, JsonNode? value, ResourceRecord record) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("section", section.Value);
            writer.WriteBoolean("overridden", overridden);
            if (overridden)
            {
                writer.WritePropertyName("override");
                if (value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    value.WriteTo(writer);
                }
            }

            WriteRecordIdentity(writer, record);
            writer.WriteEndObject();
        });

    // A platform surface with its provenance: the value code declares, the generation's override when there is one,
    // the value in force, and what the platform observes, such as an autoscaler's current replica count.
    public static byte[] WritePlatformSurface(
        ApplicationName application,
        ResourceName resource,
        ResourcePlatformSurface surface,
        ResourceRecord? record,
        JsonElement? @override,
        JsonElement value,
        JsonElement? observed) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("application", application.ToString());
            writer.WriteString("resource", resource.ToString());
            writer.WriteString("surface", surface.Key.Value);
            WriteRecordIdentity(writer, record);
            writer.WritePropertyName("schema");
            surface.Schema.WriteTo(writer);
            writer.WritePropertyName("declared");
            surface.Value.WriteTo(writer);
            writer.WritePropertyName("override");
            if (@override is JsonElement patch)
            {
                patch.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WritePropertyName("value");
            value.WriteTo(writer);
            if (observed is JsonElement platform)
            {
                writer.WritePropertyName("observed");
                platform.WriteTo(writer);
            }

            writer.WriteEndObject();
        });

    public static byte[] WriteClaims(ApplicationName owner, IReadOnlyList<string> sections, ResourceRecord record) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("owner", owner.ToString());
            writer.WriteStartArray("sections");
            foreach (string section in sections)
            {
                writer.WriteStringValue(section);
            }

            writer.WriteEndArray();
            WriteRecordIdentity(writer, record);
            writer.WriteEndObject();
        });

    // Convergence is derived here and never stored: an instance has converged when it answered, runs the build the
    // gateway's surface describes, applied the current record, and holds every section at its applied or baseline value.
    public static byte[] WriteStatus(
        ApplicationName application,
        ResourceName resource,
        ResourceRecord? record,
        ResourceSurface? surface,
        int desiredInstances,
        bool complete,
        IReadOnlyList<InstanceStatusReport> instances)
    {
        int converged = 0;
        var flags = new bool[instances.Count];
        for (int index = 0; index < instances.Count; index++)
        {
            flags[index] = IsConverged(instances[index], record, surface);
            converged += flags[index] ? 1 : 0;
        }

        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("application", application.ToString());
            writer.WriteString("resource", resource.ToString());
            WriteRecordIdentity(writer, record);
            WriteOptional(writer, "surfaceDigest", surface?.Digest);
            writer.WriteNumber("desiredInstances", desiredInstances);
            writer.WriteBoolean("complete", complete);
            writer.WriteNumber("converged", converged);
            writer.WriteNumber("total", instances.Count);
            writer.WriteStartArray("instances");
            for (int index = 0; index < instances.Count; index++)
            {
                InstanceStatusReport instance = instances[index];
                writer.WriteStartObject();
                WriteOptional(writer, "address", instance.Address);
                WriteOptional(writer, "instance", instance.Instance);
                writer.WriteBoolean("reachable", instance.Reachable);
                writer.WriteBoolean("converged", flags[index]);
                if (instance.Error is not null)
                {
                    writer.WriteString("error", instance.Error);
                }

                if (instance.Reachable)
                {
                    WriteOptional(writer, "surfaceDigest", instance.SurfaceDigest);
                    if (surface is not null)
                    {
                        writer.WriteBoolean("surfaceMatches", string.Equals(instance.SurfaceDigest, surface.Digest, StringComparison.Ordinal));
                    }

                    WriteOptional(writer, "recordDigest", instance.RecordDigest);
                    writer.WriteStartArray("sections");
                    foreach (InstanceSectionReport section in instance.Sections)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("path", section.Path);
                        writer.WriteString("state", section.State);
                        if (section.Error is not null)
                        {
                            writer.WriteString("error", section.Error);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static byte[] WriteActionResults(string action, ResourceSurfaceActionTarget target, IReadOnlyList<InstanceActionOutcome> results)
    {
        int succeeded = 0;
        foreach (InstanceActionOutcome result in results)
        {
            succeeded += result.Succeeded ? 1 : 0;
        }

        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WriteString("target", target == ResourceSurfaceActionTarget.EveryInstance ? "everyInstance" : "anyInstance");
            writer.WriteNumber("succeeded", succeeded);
            writer.WriteNumber("total", results.Count);
            writer.WriteStartArray("results");
            foreach (InstanceActionOutcome result in results)
            {
                writer.WriteStartObject();
                WriteOptional(writer, "address", result.Address);
                writer.WriteBoolean("succeeded", result.Succeeded);
                if (result.Status is int status)
                {
                    writer.WriteNumber("status", status);
                }

                if (result.Result is JsonElement value)
                {
                    writer.WritePropertyName("result");
                    value.WriteTo(writer);
                }

                if (result.Error is not null)
                {
                    writer.WriteString("error", result.Error);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static byte[] WriteInventory(ResourceInstanceInventory inventory) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("incarnation", inventory.Incarnation.ToString("D"));
            writer.WriteString("revision", inventory.Revision);
            writer.WriteNumber("desiredInstances", inventory.DesiredInstances);
            writer.WriteBoolean("complete", inventory.IsComplete);
            writer.WriteStartArray("instances");
            foreach (ResourceManagementTarget target in inventory.Instances)
            {
                writer.WriteStartObject();
                writer.WriteString("hostId", target.HostId);
                writer.WriteString("generation", target.Generation);
                writer.WriteString("address", target.Address.AbsoluteUri);
                writer.WriteBoolean("terminating", target.IsTerminating);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    // A section-level patch: indented, so an operator can read the record files.
    public static byte[] WriteLayer(JsonObject layer)
    {
        if (layer.Count == 0)
        {
            return Array.Empty<byte>();
        }

        return Write(writer => layer.WriteTo(writer));
    }

    private static bool IsConverged(InstanceStatusReport instance, ResourceRecord? record, ResourceSurface? surface)
    {
        if (!instance.Reachable ||
            record is null ||
            !string.Equals(instance.RecordDigest, record.Digest, StringComparison.Ordinal) ||
            (surface is not null && !string.Equals(instance.SurfaceDigest, surface.Digest, StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (InstanceSectionReport section in instance.Sections)
        {
            if (section.State is not ("applied" or "baseline"))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteRecordIdentity(Utf8JsonWriter writer, ResourceRecord? record)
    {
        WriteOptional(writer, "generation", record?.Generation);
        WriteOptional(writer, "version", record?.Version);
        WriteOptional(writer, "recordDigest", record?.Digest);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteLayerMember(Utf8JsonWriter writer, string name, JsonDocument? layer, string path)
    {
        if (layer is not null && layer.RootElement.TryGetProperty(path, out JsonElement patch))
        {
            writer.WritePropertyName(name);
            patch.WriteTo(writer);
        }
    }

    private static void AddUndeclared(JsonDocument? layer, HashSet<string> declared, SortedSet<string> undeclared)
    {
        if (layer is null)
        {
            return;
        }

        foreach (JsonProperty section in layer.RootElement.EnumerateObject())
        {
            if (!declared.Contains(section.Name))
            {
                undeclared.Add(section.Name);
            }
        }
    }

    private static JsonDocument? ParseLayer(ReadOnlyMemory<byte>? layer) =>
        layer is ReadOnlyMemory<byte> content && !content.IsEmpty ? JsonDocument.Parse(content) : null;

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, _indented))
        {
            write(writer);
        }

        return stream.ToArray();
    }
}
