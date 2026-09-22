using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

/// <summary>Hashes evaluated publish inputs and validates the cached image before an incremental skip.</summary>
// Deviates from the repo interface-first rule per work-item requirements: MSBuild discovers public Task subclasses.
public sealed class CohesionImageFingerprint : Task
{
    /// <summary>Gets or sets files contributing to the published payload and manifest.</summary>
    public ITaskItem[] Files { get; set; } = [];
    /// <summary>Gets or sets evaluated container options and metadata.</summary>
    public string Options { get; set; } = string.Empty;
    /// <summary>Gets or sets the image index path.</summary>
    [Required]
    public string ImagePath { get; set; } = string.Empty;
    /// <summary>Gets the current publish-input fingerprint.</summary>
    [Output]
    public string Fingerprint { get; private set; } = string.Empty;
    /// <summary>Gets whether verified current image outputs may be reused.</summary>
    [Output]
    public bool IsCurrent { get; private set; }

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(Options + "\0"));
            foreach (string path in Files.Select(file => Path.GetFullPath(file.ItemSpec)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
                using FileStream stream = File.OpenRead(path);
                hash.AppendData(SHA256.HashData(stream));
            }
            Fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!File.Exists(ImagePath + ".inputs") || File.ReadAllText(ImagePath + ".inputs").Trim() != Fingerprint || !File.Exists(ImagePath))
            {
                return true;
            }

            using JsonDocument image = ImageIndexFile.Read(ImagePath);
            if (!File.Exists(ImagePath + ".sha256") || File.ReadAllText(ImagePath + ".sha256").Trim() != Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ImagePath))))
            {
                return true;
            }

            if (image.RootElement.TryGetProperty("archive", out JsonElement archive))
            {
                ImageIndexFile.VerifyArchive(ImageIndexFile.ResolveArchive(ImagePath, archive.GetString()!), ImageIndexFile.Required(image.RootElement, "digest"));
            }
            else if (!image.RootElement.TryGetProperty("registry", out JsonElement registry) || registry.ValueKind != JsonValueKind.String)
            {
                return true;
            }

            IsCurrent = true;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.Collections.Generic.KeyNotFoundException)
        {
            if (Fingerprint.Length == 0)
            {
                Log.LogError($"Cannot fingerprint published image inputs: {exception.Message}");
                return false;
            }
            Log.LogMessage(MessageImportance.Low, $"Image cache cannot be reused: {exception.Message}");
            return true;
        }
    }
}
