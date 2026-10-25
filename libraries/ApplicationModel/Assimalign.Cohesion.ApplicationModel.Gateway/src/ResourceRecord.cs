using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// One resource's deployment record as its platform keeps it: the deployment generation the record belongs to, the
/// bytes of its layer files, and the gateway's own overrides of the resource's platform surfaces.
/// </summary>
/// <remarks>
/// <para>
/// The layers are the runtime contract's: <c>deployment.json</c> from the application's code, one
/// <c>claims.&lt;owner&gt;.json</c> per claiming application, and <c>override.json</c> from the management API. An
/// empty layer is a file the record does not have.
/// </para>
/// <para>
/// Platform overrides change platform objects, such as a Kubernetes autoscaler, not the resource's configuration, so
/// they are never delivered to the resource's instances and are outside <see cref="Digest"/>. They follow the
/// override layer's lifetime: they belong to the generation, and a new generation drops them.
/// </para>
/// <para>
/// A generation is the hash of the resource's deployment content: its plan, which carries the deployment layer, and
/// the build it deploys. Overrides belong to one generation; a deployment that changes the content starts another.
/// </para>
/// </remarks>
[DebuggerDisplay("{Generation,nq} {Digest,nq}")]
public sealed class ResourceRecord
{
    private static readonly IReadOnlyDictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> _noPlatformOverrides =
        new ReadOnlyDictionary<ResourceExtensionKey, ReadOnlyMemory<byte>>(new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>>());

    private ResourceRecord(
        string generation,
        ReadOnlyMemory<byte> deployment,
        IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> claims,
        ReadOnlyMemory<byte> @override,
        IReadOnlyDictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> platformOverrides,
        string digest)
    {
        Generation = generation;
        Deployment = deployment;
        Claims = claims;
        Override = @override;
        PlatformOverrides = platformOverrides;
        Digest = digest;
        Version = ComputeVersion(generation, digest, platformOverrides);
    }

    /// <summary>Gets the deployment generation the record belongs to.</summary>
    public string Generation { get; }

    /// <summary>Gets the deployment layer, or empty content when the record has none.</summary>
    public ReadOnlyMemory<byte> Deployment { get; }

    /// <summary>Gets the claim layers, keyed by the application that owns each.</summary>
    public IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> Claims { get; }

    /// <summary>Gets the management override layer, or empty content when the record has none.</summary>
    public ReadOnlyMemory<byte> Override { get; }

    /// <summary>
    /// Gets the management overrides of the resource's platform surfaces, keyed by surface, each an RFC 7396 merge
    /// patch over the value the application's code declares.
    /// </summary>
    public IReadOnlyDictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> PlatformOverrides { get; }

    /// <summary>
    /// Gets the record digest, <c>sha256:&lt;hex&gt;</c>, by the runtime contract's rule: the value an instance
    /// reports as its <c>recordDigest</c> once it has applied these files.
    /// </summary>
    public string Digest { get; }

    /// <summary>
    /// Gets the record's version, which changes whenever its generation, any layer or any platform override changes.
    /// The management API returns it as the entity tag that a conditional write names in <c>If-Match</c>.
    /// </summary>
    public string Version { get; }

    /// <summary>Creates a record from its generation and the contents of its layer files.</summary>
    /// <param name="generation">The deployment generation: lowercase ASCII letters, digits and hyphens, at most 63 characters.</param>
    /// <param name="deployment">The deployment layer, or empty content for none.</param>
    /// <param name="claims">The claim layers keyed by owning application; each must be nonempty.</param>
    /// <param name="override">The override layer, or empty content for none.</param>
    /// <returns>The record, without platform overrides; every layer is copied.</returns>
    /// <exception cref="ArgumentException"><paramref name="generation"/> is invalid, or a claim layer is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> or <paramref name="claims"/> is null.</exception>
    /// <exception cref="InvalidDataException">A layer is larger than 1 MiB or is not a JSON object.</exception>
    public static ResourceRecord Create(
        string generation,
        ReadOnlyMemory<byte> deployment,
        IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> claims,
        ReadOnlyMemory<byte> @override) =>
        Create(generation, deployment, claims, @override, platformOverrides: null);

    /// <summary>Creates a record from its generation, the contents of its layer files and its platform overrides.</summary>
    /// <param name="generation">The deployment generation: lowercase ASCII letters, digits and hyphens, at most 63 characters.</param>
    /// <param name="deployment">The deployment layer, or empty content for none.</param>
    /// <param name="claims">The claim layers keyed by owning application; each must be nonempty.</param>
    /// <param name="override">The override layer, or empty content for none.</param>
    /// <param name="platformOverrides">The platform surface overrides keyed by surface, or null for none; each must be nonempty JSON.</param>
    /// <returns>The record; every layer and override is copied.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="generation"/> is invalid, a claim layer or a platform override is empty, or a platform key is the default value.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> or <paramref name="claims"/> is null.</exception>
    /// <exception cref="InvalidDataException">A layer or a platform override is larger than 1 MiB or is not JSON of the expected shape.</exception>
    public static ResourceRecord Create(
        string generation,
        ReadOnlyMemory<byte> deployment,
        IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> claims,
        ReadOnlyMemory<byte> @override,
        IReadOnlyDictionary<ResourceExtensionKey, ReadOnlyMemory<byte>>? platformOverrides)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(claims);
        if (!IsGeneration(generation))
        {
            throw new ArgumentException(ErrorMessages.ResourceRecordGenerationInvalid, nameof(generation));
        }

        var claimCopy = new Dictionary<ApplicationName, ReadOnlyMemory<byte>>(claims.Count);
        var claimLayers = new List<KeyValuePair<string, byte[]>>(claims.Count);
        foreach ((ApplicationName owner, ReadOnlyMemory<byte> layer) in claims)
        {
            if (layer.IsEmpty)
            {
                throw new ArgumentException(ErrorMessages.ResourceRecordClaimEmpty, nameof(claims));
            }

            byte[] copy = layer.ToArray();
            claimCopy.Add(owner, copy);
            claimLayers.Add(KeyValuePair.Create(owner.ToString(), copy));
        }

        IReadOnlyDictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> platformCopy = _noPlatformOverrides;
        if (platformOverrides is { Count: > 0 })
        {
            var copies = new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>>(platformOverrides.Count);
            foreach ((ResourceExtensionKey key, ReadOnlyMemory<byte> patch) in platformOverrides)
            {
                if (key.Value is null || patch.IsEmpty)
                {
                    throw new ArgumentException(ErrorMessages.ResourceRecordPlatformOverrideInvalid, nameof(platformOverrides));
                }

                copies.Add(key, RequireJson(patch));
            }

            platformCopy = new ReadOnlyDictionary<ResourceExtensionKey, ReadOnlyMemory<byte>>(copies);
        }

        byte[]? deploymentLayer = deployment.IsEmpty ? null : deployment.ToArray();
        byte[]? overrideLayer = @override.IsEmpty ? null : @override.ToArray();
        ResourceConfigurationRecord record = ResourceConfigurationRecord.Create(deploymentLayer, claimLayers, overrideLayer);
        return new ResourceRecord(
            generation,
            deploymentLayer,
            new ReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>>(claimCopy),
            overrideLayer,
            platformCopy,
            record.Digest);
    }

    /// <summary>
    /// Computes the digest of what the record sets, through overrides and claims, for sections an instance applies
    /// only when it starts.
    /// </summary>
    /// <param name="surface">The resource's runtime surface, or null when its build described none.</param>
    /// <returns>
    /// <c>sha256:&lt;hex&gt;</c>, or <see langword="null"/> when no override or claim sets a restart section. A platform
    /// that restarts instances for such a change, such as a Kubernetes rollout keyed by a pod template annotation,
    /// compares the digests of two records.
    /// </returns>
    /// <remarks>
    /// The deployment layer is left out: it changes only with the resource's plan, which redeploys the resource
    /// anyway. A deployment hands the record's digest to the platform as <see cref="ResourceInputs.RestartDigest"/>.
    /// </remarks>
    public string? GetRestartDigest(ResourceSurface? surface)
    {
        if (surface is null)
        {
            return null;
        }

        var sections = new List<string>();
        foreach (ResourceSurfaceSection section in surface.Configuration)
        {
            if (section.Mutability == ResourceSurfaceMutability.Restart)
            {
                sections.Add(section.Path.Value);
            }
        }

        if (sections.Count == 0)
        {
            return null;
        }

        sections.Sort(StringComparer.Ordinal);
        JsonObject? overrides = Override.IsEmpty ? null : JsonNode.Parse(Override.Span) as JsonObject;
        var owners = new List<ApplicationName>(Claims.Keys);
        owners.Sort(static (left, right) => string.CompareOrdinal(left.ToString(), right.ToString()));
        var claims = new List<(string Owner, JsonObject Layer)>(owners.Count);
        foreach (ApplicationName owner in owners)
        {
            if (JsonNode.Parse(Claims[owner].Span) is JsonObject layer)
            {
                claims.Add((owner.ToString(), layer));
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool any = false;
        foreach (string path in sections)
        {
            if (overrides is not null && overrides.TryGetPropertyValue(path, out JsonNode? value))
            {
                Append(hash, "override\0" + path + "\0" + (value?.ToJsonString() ?? "null"));
                any = true;
            }

            foreach ((string owner, JsonObject layer) in claims)
            {
                if (layer.TryGetPropertyValue(path, out JsonNode? claimed))
                {
                    Append(hash, "claims\0" + owner + "\0" + path + "\0" + (claimed?.ToJsonString() ?? "null"));
                    any = true;
                }
            }
        }

        return any ? "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()) : null;
    }

    // A record without platform overrides keeps the version it had before they existed.
    private static string ComputeVersion(
        string generation,
        string digest,
        IReadOnlyDictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> platformOverrides)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(generation + "\n" + digest));
        if (platformOverrides.Count > 0)
        {
            var keys = new List<ResourceExtensionKey>(platformOverrides.Keys);
            keys.Sort(static (left, right) => string.CompareOrdinal(left.Value, right.Value));
            Span<byte> length = stackalloc byte[sizeof(long)];
            foreach (ResourceExtensionKey key in keys)
            {
                ReadOnlyMemory<byte> patch = platformOverrides[key];
                hash.AppendData(Encoding.UTF8.GetBytes("\n" + key.Value + "\0"));
                BinaryPrimitives.WriteInt64BigEndian(length, patch.Length);
                hash.AppendData(length);
                hash.AppendData(patch.Span);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 16));
    }

    // Platform overrides are the gateway's own data, never an instance's file, so they are kept compact: how a store
    // formats them cannot move the version.
    private static byte[] RequireJson(ReadOnlyMemory<byte> content)
    {
        if (content.Length > ResourceConfigurationRecord.MaximumLayerLength)
        {
            throw new InvalidDataException(ErrorMessages.ResourceRecordPlatformOverrideInvalid);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                document.RootElement.WriteTo(writer);
            }

            return stream.ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(ErrorMessages.ResourceRecordPlatformOverrideInvalid, exception);
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    // A DNS label, so a platform can name per-generation objects after it.
    private static bool IsGeneration(string value)
    {
        if (value.Length is 0 or > 63 || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }
}
