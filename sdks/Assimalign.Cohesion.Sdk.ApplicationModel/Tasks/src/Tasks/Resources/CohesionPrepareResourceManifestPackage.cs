using System;
using System.IO;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

/// <summary>
/// Creates the portable resource manifest used by a Cohesion manifest package.
/// </summary>
public sealed class CohesionPrepareResourceManifestPackage : Task
{
    /// <summary>Gets or sets the build-time resource manifest path.</summary>
    [Required]
    public string SourceManifestPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the portable package manifest output path.</summary>
    [Required]
    public string PackageManifestPath { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            using FileStream source = File.OpenRead(SourceManifestPath);
            using JsonDocument document = JsonDocument.Parse(source);
            using var output = new MemoryStream();
            using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });

            WritePortableManifest(writer, document.RootElement);
            writer.Flush();
            ResourceFileWriter.WriteIfChanged(
                PackageManifestPath,
                new ReadOnlySpan<byte>(output.GetBuffer(), 0, checked((int)output.Length)));
            return true;
        }
        catch (JsonException exception)
        {
            Log.LogError($"Cohesion resource manifest '{SourceManifestPath}' is invalid JSON: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            Log.LogError(exception.Message);
        }
        catch (IOException exception)
        {
            Log.LogError($"Could not prepare Cohesion resource manifest package: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.LogError($"Could not prepare Cohesion resource manifest package: {exception.Message}");
        }

        return false;
    }

    private static void WritePortableManifest(Utf8JsonWriter writer, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A Cohesion resource manifest must have a JSON object root.");
        }

        bool foundArtifact = false;
        writer.WriteStartObject();
        foreach (JsonProperty property in root.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.NameEquals("artifact"))
            {
                WritePortableArtifact(writer, property.Value);
                foundArtifact = true;
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }
        writer.WriteEndObject();

        if (!foundArtifact)
        {
            throw new InvalidDataException("A Cohesion resource manifest must contain an artifact object.");
        }
    }

    private static void WritePortableArtifact(Utf8JsonWriter writer, JsonElement artifact)
    {
        if (artifact.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A Cohesion resource manifest artifact must be a JSON object.");
        }

        writer.WriteStartObject();
        foreach (JsonProperty property in artifact.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.NameEquals("project") || property.NameEquals("apphost"))
            {
                writer.WriteNullValue();
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }
}
