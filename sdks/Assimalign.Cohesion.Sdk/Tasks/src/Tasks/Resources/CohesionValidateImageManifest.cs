using System;
using System.IO;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.Tasks;

/// <summary>
/// Determines whether a Cohesion image manifest contains a digest-pinned image reference.
/// </summary>
public sealed class CohesionValidateImageManifest : Task
{
    /// <summary>Gets or sets the image manifest path to validate.</summary>
    [Required]
    public string ImageManifestPath { get; set; } = string.Empty;

    /// <summary>Gets whether the image manifest contains a valid SHA-256 digest.</summary>
    [Output]
    public bool HasDigestPinnedImage { get; private set; }

    /// <inheritdoc />
    public override bool Execute()
    {
        HasDigestPinnedImage = false;
        if (!File.Exists(ImageManifestPath))
        {
            return true;
        }

        try
        {
            using FileStream source = File.OpenRead(ImageManifestPath);
            using JsonDocument document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("digest", out JsonElement digestElement)
                || digestElement.ValueKind != JsonValueKind.String)
            {
                return true;
            }

            string? digest = digestElement.GetString();
            HasDigestPinnedImage = IsSha256Digest(digest);
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
        catch (IOException exception)
        {
            Log.LogError($"Could not read Cohesion image manifest '{ImageManifestPath}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.LogError($"Could not read Cohesion image manifest '{ImageManifestPath}': {exception.Message}");
        }

        return false;
    }

    private static bool IsSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null
            || digest.Length != prefix.Length + 64
            || !digest.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in digest.AsSpan(prefix.Length))
        {
            bool isDigit = character is >= '0' and <= '9';
            bool isLowercaseHex = character is >= 'a' and <= 'f';
            bool isUppercaseHex = character is >= 'A' and <= 'F';
            if (!isDigit && !isLowercaseHex && !isUppercaseHex)
            {
                return false;
            }
        }

        return true;
    }
}
