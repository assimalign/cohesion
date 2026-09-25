using System;
using System.IO;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Assimalign.Cohesion.Sdk.ApplicationModel.Tasks.Internal;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

/// <summary>Rebases an image index to the manifest package's contained archive slot.</summary>
// Deviates from the repo interface-first rule per work-item requirements: MSBuild discovers public Task subclasses.
public sealed class CohesionPrepareImagePackage : Task
{
    /// <summary>Gets or sets the source image document.</summary>
    [Required]
    public string SourcePath { get; set; } = string.Empty;
    /// <summary>Gets or sets the staged package document.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;
    /// <summary>Gets or sets the archive selected for the air-gapped package, or empty when not packing one.</summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(SourcePath));
            if (ArchivePath.Length != 0 && document.RootElement.TryGetProperty("resource", out _))
            {
                string relative = ImageIndexFile.Required(document.RootElement, "archive");
                string publishedArchive = ImageIndexFile.ResolveArchive(SourcePath, relative);
                if (!string.Equals(publishedArchive, Path.GetFullPath(ArchivePath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("CohesionPackImageArchive must use the archive recorded by this image publish, not another or stale archive.");
                }
                ImageIndexFile.VerifyArchive(ArchivePath, ImageIndexFile.Required(document.RootElement, "digest"));
            }
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "archive")
                {
                    property.WriteTo(writer);
                }
            }
            if (ArchivePath.Length != 0)
            {
                writer.WriteString("archive", "images/" + Path.GetFileName(ArchivePath));
            }
            writer.WriteEndObject();
            writer.Flush();
            ResourceFileWriter.WriteIfChanged(OutputPath, stream.ToArray());
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            Log.LogError($"Cannot prepare packaged image index: {exception.Message}");
            return false;
        }
    }
}
