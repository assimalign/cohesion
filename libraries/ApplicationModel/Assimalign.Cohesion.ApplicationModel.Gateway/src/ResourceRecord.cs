using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// One resource's deployment record as its platform keeps it: the deployment generation the record belongs to and the
/// bytes of its layer files.
/// </summary>
/// <remarks>
/// <para>
/// The layers are the runtime contract's: <c>deployment.json</c> from the application's code, one
/// <c>claims.&lt;owner&gt;.json</c> per claiming application, and <c>override.json</c> from the management API. An
/// empty layer is a file the record does not have.
/// </para>
/// <para>
/// A generation is the hash of the resource's deployment content: its plan, which carries the deployment layer, and
/// the build it deploys. Overrides belong to one generation; a deployment that changes the content starts another.
/// </para>
/// </remarks>
[DebuggerDisplay("{Generation,nq} {Digest,nq}")]
public sealed class ResourceRecord
{
    private ResourceRecord(
        string generation,
        ReadOnlyMemory<byte> deployment,
        IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> claims,
        ReadOnlyMemory<byte> @override,
        string digest)
    {
        Generation = generation;
        Deployment = deployment;
        Claims = claims;
        Override = @override;
        Digest = digest;
        byte[] version = SHA256.HashData(Encoding.UTF8.GetBytes(generation + "\n" + digest));
        Version = Convert.ToHexStringLower(version.AsSpan(0, 16));
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
    /// Gets the record digest, <c>sha256:&lt;hex&gt;</c>, by the runtime contract's rule: the value an instance
    /// reports as its <c>recordDigest</c> once it has applied these files.
    /// </summary>
    public string Digest { get; }

    /// <summary>
    /// Gets the record's version, which changes whenever its generation or any layer changes. The management API
    /// returns it as the entity tag that a conditional write names in <c>If-Match</c>.
    /// </summary>
    public string Version { get; }

    /// <summary>Creates a record from its generation and the contents of its layer files.</summary>
    /// <param name="generation">The deployment generation: lowercase ASCII letters, digits and hyphens, at most 63 characters.</param>
    /// <param name="deployment">The deployment layer, or empty content for none.</param>
    /// <param name="claims">The claim layers keyed by owning application; each must be nonempty.</param>
    /// <param name="override">The override layer, or empty content for none.</param>
    /// <returns>The record; every layer is copied.</returns>
    /// <exception cref="ArgumentException"><paramref name="generation"/> is invalid, or a claim layer is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> or <paramref name="claims"/> is null.</exception>
    /// <exception cref="System.IO.InvalidDataException">A layer is larger than 1 MiB or is not a JSON object.</exception>
    public static ResourceRecord Create(
        string generation,
        ReadOnlyMemory<byte> deployment,
        IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> claims,
        ReadOnlyMemory<byte> @override)
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

        byte[]? deploymentLayer = deployment.IsEmpty ? null : deployment.ToArray();
        byte[]? overrideLayer = @override.IsEmpty ? null : @override.ToArray();
        ResourceConfigurationRecord record = ResourceConfigurationRecord.Create(deploymentLayer, claimLayers, overrideLayer);
        return new ResourceRecord(
            generation,
            deploymentLayer,
            new ReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>>(claimCopy),
            overrideLayer,
            record.Digest);
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
