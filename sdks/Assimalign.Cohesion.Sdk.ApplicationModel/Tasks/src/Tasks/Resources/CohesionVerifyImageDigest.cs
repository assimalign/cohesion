using System;
using System.IO;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Assimalign.Cohesion.Sdk.ApplicationModel.Tasks.Internal;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

/// <summary>Verifies the single SDK-produced digest against its selected output sink.</summary>
// Deviates from the repo interface-first rule per work-item requirements: MSBuild discovers public Task subclasses.
public sealed class CohesionVerifyImageDigest : Task
{
    /// <summary>Gets or sets GeneratedContainerDigest from the SDK container task.</summary>
    [Required]
    public string Digest { get; set; } = string.Empty;
    /// <summary>Gets or sets the selected OCI archive, or empty for a registry sink.</summary>
    public string ArchivePath { get; set; } = string.Empty;
    /// <summary>Gets or sets the digest returned by the successful registry publish.</summary>
    public string RegistryDigest { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            string digest = ImageIndexFile.Digest(Digest);
            if (ArchivePath.Length != 0)
            {
                ImageIndexFile.VerifyArchive(ArchivePath, digest);
            }
            else if (digest != ImageIndexFile.Digest(RegistryDigest))
            {
                throw new InvalidDataException("GeneratedContainerDigest differs from the registry's returned digest.");
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.Collections.Generic.KeyNotFoundException)
        {
            Log.LogError($"Image digest verification failed: {exception.Message}");
            return false;
        }
    }
}
