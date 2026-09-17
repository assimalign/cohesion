using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.Gateway.Tasks;

/// <summary>Gathers ordered resource indexes and relocates verified archives beneath the application index.</summary>
// Deviates from the repo interface-first rule per work-item requirements: MSBuild discovers public Task subclasses.
public sealed class CohesionGatherImageIndexes : Task
{
    /// <summary>Gets or sets the application identity.</summary>
    [Required]
    public string Application { get; set; } = string.Empty;
    /// <summary>Gets or sets the application index destination beside the gateway publish output.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;
    /// <summary>Gets or sets the gateway project directory.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;
    /// <summary>Gets or sets resource declarations in source order.</summary>
    public ITaskItem[] ResourceReferences { get; set; } = [];
    /// <summary>Gets or sets published project image indexes, with ProjectFullPath metadata.</summary>
    public ITaskItem[] ProjectImages { get; set; } = [];
    /// <summary>Gets or sets restored package resource manifests with ReferenceIdentity metadata.</summary>
    public ITaskItem[] PackageManifests { get; set; } = [];
    /// <summary>Gets or sets the composite image when InProcess is the only provider.</summary>
    public string CompositeImage { get; set; } = string.Empty;
    /// <summary>Gets or sets whether InProcess is the entire selected provider set.</summary>
    public bool OnlyComposite { get; set; }

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Application))
            {
                throw new InvalidDataException("CohesionApplicationName must be non-empty for CohesionPublishImages.");
            }
            var paths = new List<string>();
            if (!OnlyComposite)
            {
                foreach (ITaskItem reference in ResourceReferences)
                {
                    string path;
                    if (reference.ItemSpec.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        string full = Path.GetFullPath(reference.ItemSpec, ProjectDirectory);
                        ITaskItem? image = ProjectImages.FirstOrDefault(item => string.Equals(
                            Path.GetFullPath(item.GetMetadata("ProjectFullPath")), full, StringComparison.OrdinalIgnoreCase));
                        path = image?.ItemSpec ?? throw new InvalidDataException($"No published image was returned for resource project '{full}'.");
                    }
                    else
                    {
                        ITaskItem? manifest = PackageManifests.FirstOrDefault(item => string.Equals(
                            item.GetMetadata("ReferenceIdentity"), reference.ItemSpec, StringComparison.OrdinalIgnoreCase));
                        if (manifest is null)
                        {
                            throw new InvalidDataException($"Pinned resource '{reference.ItemSpec}' has no restored manifest package.");
                        }
                        path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest.ItemSpec))!, "image.json");
                    }
                    paths.Add(path);
                }
                foreach (ITaskItem image in ProjectImages)
                {
                    if (!paths.Contains(image.ItemSpec, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(image.ItemSpec);
                    }
                }
            }
            if (CompositeImage.Length != 0)
            {
                paths.Add(CompositeImage);
            }

            var resources = new HashSet<string>(StringComparer.Ordinal);
            var images = new List<(JsonDocument Document, string? Archive, string? Source)>();
            try
            {
                foreach (string path in paths)
                {
                    JsonDocument document = ImageIndexFile.Read(path);
                    images.Add((document, null, null));
                    JsonElement root = document.RootElement;
                    string resource = ImageIndexFile.Required(root, "resource");
                    if (!resources.Add(resource))
                    {
                        throw new InvalidDataException($"Duplicate image resource '{resource}' in application '{Application}'.");
                    }
                    if (root.TryGetProperty("archive", out JsonElement archive))
                    {
                        string source = ImageIndexFile.ResolveArchive(path, archive.GetString()!);
                        ImageIndexFile.VerifyArchive(source, ImageIndexFile.Required(root, "digest"));
                        // An ordinal directory avoids filename collisions and never treats resource names as paths.
                        string relative = $"images/{images.Count - 1}/{Path.GetFileName(source)}";
                        ImageIndexFile.ResolveArchive(OutputPath, relative);
                        images[^1] = (document, relative, source);
                    }
                }

                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("schema", "cohesion/images/v1");
                writer.WriteString("application", Application);
                writer.WriteStartArray("images");
                foreach ((JsonDocument document, string? archive, string? source) in images)
                {
                    if (archive is not null)
                    {
                        string destination = ImageIndexFile.ResolveArchive(OutputPath, archive);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        if (!string.Equals(Path.GetFullPath(source!), destination, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(source!, destination, overwrite: true);
                        }
                    }
                    writer.WriteStartObject();
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (property.Name is not ("schema" or "archive"))
                        {
                            property.WriteTo(writer);
                        }
                    }
                    if (archive is not null)
                    {
                        writer.WriteString("archive", archive);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                ResourceFileWriter.WriteIfChanged(OutputPath, stream.ToArray());
            }
            finally
            {
                foreach (var image in images)
                {
                    image.Document.Dispose();
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Log.LogError($"Cannot gather application images: {exception.Message}");
            return false;
        }
    }
}
