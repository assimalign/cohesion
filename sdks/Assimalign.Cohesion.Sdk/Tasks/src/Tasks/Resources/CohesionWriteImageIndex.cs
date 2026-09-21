using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.Tasks;

/// <summary>Writes the frozen cohesion/image/v1 producer document after digest verification.</summary>
// Deviates from the repo interface-first rule per work-item requirements: MSBuild discovers public Task subclasses.
public sealed class CohesionWriteImageIndex : Task
{
    /// <summary>Gets or sets the output document path.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;
    /// <summary>Gets or sets the owning resource name.</summary>
    [Required]
    public string Resource { get; set; } = string.Empty;
    /// <summary>Gets or sets the repository without its registry.</summary>
    [Required]
    public string Repository { get; set; } = string.Empty;
    /// <summary>Gets or sets the authority pinned by a successful push, or empty for late binding.</summary>
    public string Registry { get; set; } = string.Empty;
    /// <summary>Gets or sets the optional human-readable version.</summary>
    public string Tag { get; set; } = string.Empty;
    /// <summary>Gets or sets the verified immutable digest.</summary>
    [Required]
    public string Digest { get; set; } = string.Empty;
    /// <summary>Gets or sets whether the entry point is NativeAOT.</summary>
    public bool Aot { get; set; }
    /// <summary>Gets or sets the Linux runtime identifier the payload was published for; it selects the recorded OCI platform.</summary>
    public string RuntimeIdentifier { get; set; } = "linux-x64";
    /// <summary>Gets or sets the base-image identity used by the publisher.</summary>
    [Required]
    public string BaseImage { get; set; } = string.Empty;
    /// <summary>Gets or sets the archive produced in this build, or empty for a registry sink.</summary>
    public string ArchivePath { get; set; } = string.Empty;
    /// <summary>Gets or sets the evaluated publish-input fingerprint for incremental reuse.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            if (!ImageRuntimeIdentifiers.IsSupported(RuntimeIdentifier))
            {
                throw new InvalidDataException($"Cohesion images support {ImageRuntimeIdentifiers.SupportedList} RuntimeIdentifier.");
            }

            ImageIndexFile.Repository(Repository);
            string digest = ImageIndexFile.Digest(Digest);
            if (Registry.Length != 0)
            {
                ImageIndexFile.Registry(Registry);
            }

            if (Registry.Length == 0 && ArchivePath.Length == 0)
            {
                throw new InvalidDataException("A late-bound image requires a verified local archive.");
            }

            string? archive = ArchivePath.Length == 0 ? null : ImageIndexFile.RelativeArchive(OutputPath, ArchivePath);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("schema", "cohesion/image/v1");
            writer.WriteString("resource", Resource);
            writer.WriteString("repository", Repository);
            writer.WriteString("registry", Registry.Length == 0 ? null : Registry);
            writer.WriteString("tag", Tag.Length == 0 ? null : Tag);
            writer.WriteString("digest", digest);
            writer.WriteString("platform", ImageRuntimeIdentifiers.GetOciPlatform(RuntimeIdentifier));
            writer.WriteBoolean("aot", Aot);
            writer.WriteString("baseImage", BaseImage);
            if (archive is not null)
            {
                writer.WriteString("archive", archive);
            }

            writer.WriteEndObject();
            writer.Flush();
            ResourceFileWriter.WriteIfChanged(OutputPath, stream.ToArray());
            ResourceFileWriter.WriteIfChanged(OutputPath + ".inputs", Encoding.UTF8.GetBytes(Fingerprint));
            ResourceFileWriter.WriteIfChanged(OutputPath + ".sha256", Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()))));
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.LogError(exception.Message);
            return false;
        }
    }
}
