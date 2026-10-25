using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Reports which deployment record this instance applied and the outcome for each configuration section.
/// </summary>
/// <remarks>
/// A gateway derives convergence by comparing <see cref="RecordDigest"/> on every instance with the digest
/// of the record it last delivered. Nothing about convergence is stored anywhere.
/// </remarks>
/// <param name="RecordDigest">The digest of the applied deployment record, or null before any record was applied.</param>
/// <param name="Sections">Each section's outcome, in registration order.</param>
public sealed record ResourceConfigurationStatus(
    string? RecordDigest,
    IReadOnlyList<ResourceConfigurationSectionStatus> Sections)
{
    /// <summary>Gets the status of an instance that has applied no deployment record.</summary>
    public static ResourceConfigurationStatus Empty { get; } = new(null, Array.Empty<ResourceConfigurationSectionStatus>());

    /// <summary>Writes the status as a JSON object.</summary>
    /// <param name="writer">The writer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        if (RecordDigest is null)
        {
            writer.WriteNull("recordDigest");
        }
        else
        {
            writer.WriteString("recordDigest", RecordDigest);
        }

        writer.WriteStartArray("sections");
        foreach (ResourceConfigurationSectionStatus section in Sections)
        {
            writer.WriteStartObject();
            writer.WriteString("path", section.Path.Value);
            writer.WriteString("state", section.State switch
            {
                ResourceConfigurationState.Applied => "applied",
                ResourceConfigurationState.Rejected => "rejected",
                ResourceConfigurationState.RestartRequired => "restartRequired",
                _ => "baseline",
            });
            if (section.Error is not null)
            {
                writer.WriteString("error", section.Error);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
