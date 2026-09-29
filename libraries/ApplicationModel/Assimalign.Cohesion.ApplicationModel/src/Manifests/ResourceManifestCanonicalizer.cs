using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Produces stable hashes over the portable contract subset of resource manifests.
/// </summary>
public static class ResourceManifestCanonicalizer
{
    /// <summary>
    /// Computes a SHA-256 hash over a manifest with ordinally sorted JSON keys and
    /// machine-specific source and app-host paths removed.
    /// </summary>
    /// <param name="manifest">The manifest to hash.</param>
    /// <returns>The lower-case hexadecimal SHA-256 hash.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// <paramref name="manifest"/> does not satisfy the resource-manifest contract.
    /// </exception>
    public static string ComputeHash(ResourceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();

        return ComputeHash(GetCanonicalBytes(manifest));
    }

    /// <summary>
    /// Computes an order-independent SHA-256 hash over a resource-manifest closure.
    /// </summary>
    /// <param name="manifests">The closure manifests.</param>
    /// <returns>The lower-case hexadecimal SHA-256 hash.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifests"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="manifests"/> contains a <see langword="null"/> entry.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// A manifest does not satisfy the resource-manifest contract.
    /// </exception>
    public static string ComputeClosureHash(IReadOnlyList<ResourceManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var documents = new byte[manifests.Count][];
        for (int index = 0; index < documents.Length; index++)
        {
            ResourceManifest manifest = manifests[index]
                ?? throw new ArgumentException(
                    "The manifest closure must not contain null entries.",
                    nameof(manifests));
            manifest.Validate();
            documents[index] = GetCanonicalBytes(manifest);
        }

        Array.Sort(documents, CanonicalByteComparer.Instance);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (int index = 0; index < documents.Length; index++)
            {
                writer.WriteRawValue(documents[index], skipInputValidation: true);
            }

            writer.WriteEndArray();
        }

        return ComputeHash(WithTrailingLineFeed(buffer.WrittenSpan));
    }

    private static byte[] GetCanonicalBytes(ResourceManifest manifest)
    {
        JsonElement element = JsonSerializer.SerializeToElement(
            manifest,
            ResourceManifestJsonContext.Default.ResourceManifest);
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(element, writer, parentProperty: null);
        }

        return WithTrailingLineFeed(buffer.WrittenSpan);
    }

    private static void WriteCanonical(
        JsonElement element,
        Utf8JsonWriter writer,
        string? parentProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = new List<JsonProperty>();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!IsMachineSpecificArtifactPath(parentProperty, property.Name))
                    {
                        properties.Add(property);
                    }
                }

                properties.Sort(static (left, right) =>
                    StringComparer.Ordinal.Compare(left.Name, right.Name));

                for (int index = 0; index < properties.Count; index++)
                {
                    JsonProperty property = properties[index];
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer, property.Name);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer, parentProperty: null);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsMachineSpecificArtifactPath(string? parentProperty, string property)
    {
        return string.Equals(parentProperty, "artifact", StringComparison.Ordinal) &&
            (string.Equals(property, "project", StringComparison.Ordinal) ||
             string.Equals(property, "apphost", StringComparison.Ordinal));
    }

    private static string ComputeHash(ReadOnlySpan<byte> bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] WithTrailingLineFeed(ReadOnlySpan<byte> bytes)
    {
        var canonical = new byte[bytes.Length + 1];
        bytes.CopyTo(canonical);
        canonical[^1] = (byte)'\n';
        return canonical;
    }

    private sealed class CanonicalByteComparer : IComparer<byte[]>
    {
        public static CanonicalByteComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}
