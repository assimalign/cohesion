using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The deployment record a platform delivered to this instance: the resource's desired runtime configuration,
/// kept where the platform keeps the deployment and never in a Cohesion store.
/// </summary>
/// <remarks>
/// <para>
/// The record is a directory of up to three kinds of layer file, each a JSON object that maps section
/// paths to RFC 7396 merge patches, applied in this order:
/// </para>
/// <list type="number">
///   <item><description><c>deployment.json</c>, written by the code-first deployment.</description></item>
///   <item><description><c>claims.&lt;owner&gt;.json</c>, one per application that contributed entries, in ordinal owner order.</description></item>
///   <item><description><c>override.json</c>, written by the management API; it lasts until the resource's next deployment.</description></item>
/// </list>
/// <para>
/// Every file is optional, and a file holding an empty object counts as absent: it sets nothing. <see cref="Digest"/>
/// covers the exact bytes of every file that sets something, so the gateway that wrote them computes the same digest
/// without agreeing on any JSON canonical form.
/// </para>
/// </remarks>
[DebuggerDisplay("{Digest,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ResourceConfigurationRecord
{
    /// <summary>The file name of the deployment layer.</summary>
    public const string DeploymentLayerFileName = "deployment.json";

    /// <summary>The file name of the operator override layer.</summary>
    public const string OverrideLayerFileName = "override.json";

    /// <summary>The file name prefix of a claim layer, <c>claims.&lt;owner&gt;.json</c>.</summary>
    public const string ClaimLayerFilePrefix = "claims.";

    /// <summary>The largest layer file accepted, in bytes: the size of a Kubernetes ConfigMap.</summary>
    public const int MaximumLayerLength = 1024 * 1024;

    private const string layerFileExtension = ".json";

    private readonly JsonObject? _deployment;
    private readonly KeyValuePair<string, JsonObject>[] _claims;
    private readonly JsonObject? _override;

    private ResourceConfigurationRecord(
        string digest,
        JsonObject? deployment,
        KeyValuePair<string, JsonObject>[] claims,
        JsonObject? @override)
    {
        Digest = digest;
        _deployment = deployment;
        _claims = claims;
        _override = @override;
    }

    /// <summary>Gets the SHA-256 digest of the record's layer files, as <c>sha256:&lt;hex&gt;</c>.</summary>
    public string Digest { get; }

    /// <summary>Gets the applications whose claims the record carries, in ordinal order.</summary>
    public IReadOnlyList<string> ClaimOwners
    {
        get
        {
            var owners = new string[_claims.Length];
            for (int index = 0; index < _claims.Length; index++)
            {
                owners[index] = _claims[index].Key;
            }

            return owners;
        }
    }

    /// <summary>Loads the record from the directory the gateway delivered.</summary>
    /// <param name="directory">The record directory, normally the value of <see cref="AppEnvironment.Variables.ConfigDirectory"/>.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="InvalidDataException">A layer is larger than <see cref="MaximumLayerLength"/>, or is not a JSON object.</exception>
    /// <exception cref="IOException">A layer could not be read.</exception>
    public static ResourceConfigurationRecord Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(ErrorMessages.ResourceConfigurationDirectoryMissing);
        }

        var claims = new List<KeyValuePair<string, byte[]>>();
        foreach (string file in Directory.EnumerateFiles(directory, ClaimLayerFilePrefix + "*" + layerFileExtension))
        {
            string name = Path.GetFileName(file);
            string owner = name[ClaimLayerFilePrefix.Length..^layerFileExtension.Length];
            if (IsOwner(owner) && ReadLayer(file) is byte[] layer)
            {
                claims.Add(KeyValuePair.Create(owner, layer));
            }
        }

        return Create(
            ReadLayer(Path.Combine(directory, DeploymentLayerFileName)),
            claims,
            ReadLayer(Path.Combine(directory, OverrideLayerFileName)));
    }

    /// <summary>Creates a record from the contents of its layer files.</summary>
    /// <param name="deployment">The bytes of <c>deployment.json</c>, or null when the record has none.</param>
    /// <param name="claims">The bytes of each <c>claims.&lt;owner&gt;.json</c>, keyed by owner, in any order.</param>
    /// <param name="override">The bytes of <c>override.json</c>, or null when the record has none.</param>
    /// <returns>The record, whose <see cref="Digest"/> is the one an instance reports after applying these files.</returns>
    /// <remarks>
    /// A gateway uses this to compute the digest of the files it writes, so it compares an instance's report with
    /// the record it delivered by the same rule the instance used.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="claims"/> is null.</exception>
    /// <exception cref="InvalidDataException">A layer is too large or not a JSON object, or a claim owner repeats or is not a DNS label.</exception>
    public static ResourceConfigurationRecord Create(
        byte[]? deployment,
        IReadOnlyList<KeyValuePair<string, byte[]>> claims,
        byte[]? @override)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var ordered = new List<KeyValuePair<string, byte[]>>(claims);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        for (int index = 0; index < ordered.Count; index++)
        {
            if (!IsOwner(ordered[index].Key) || (index > 0 && ordered[index - 1].Key == ordered[index].Key))
            {
                throw new InvalidDataException(ErrorMessages.ResourceConfigurationRecordInvalid);
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        JsonObject? deploymentLayer = deployment is not null
            ? Parse(hash, DeploymentLayerFileName, deployment)
            : null;
        var claimLayers = new List<KeyValuePair<string, JsonObject>>(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            string fileName = ClaimLayerFilePrefix + ordered[index].Key + layerFileExtension;
            if (Parse(hash, fileName, ordered[index].Value) is JsonObject layer)
            {
                claimLayers.Add(KeyValuePair.Create(ordered[index].Key, layer));
            }
        }

        JsonObject? overrideLayer = @override is not null
            ? Parse(hash, OverrideLayerFileName, @override)
            : null;
        return new ResourceConfigurationRecord(
            "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()),
            deploymentLayer,
            claimLayers.ToArray(),
            overrideLayer);
    }

    /// <summary>Returns the present layers in the order they merge: deployment, claims by owner, override.</summary>
    internal IEnumerable<JsonObject> LayersInPrecedenceOrder()
    {
        if (_deployment is not null)
        {
            yield return _deployment;
        }

        foreach (KeyValuePair<string, JsonObject> claim in _claims)
        {
            yield return claim.Value;
        }

        if (_override is not null)
        {
            yield return _override;
        }
    }

    private static byte[]? ReadLayer(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        byte[] content = File.ReadAllBytes(path);
        return content.Length <= MaximumLayerLength
            ? content
            : throw new InvalidDataException(ErrorMessages.ResourceConfigurationRecordInvalid);
    }

    // The digest frames every file that sets something as: UTF-8 name, a zero byte, the big-endian 64-bit length, the
    // bytes. A file holding an empty object sets nothing, so it counts as absent and returns null: a platform that can
    // overwrite an instance's files but not delete them, such as Docker's archive API, withdraws a layer by writing {}.
    private static JsonObject? Parse(IncrementalHash hash, string fileName, byte[] content)
    {
        if (content.Length > MaximumLayerLength)
        {
            throw new InvalidDataException(ErrorMessages.ResourceConfigurationRecordInvalid);
        }

        JsonObject layer;
        try
        {
            layer = JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidDataException(ErrorMessages.ResourceConfigurationRecordInvalid);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(ErrorMessages.ResourceConfigurationRecordInvalid, exception);
        }

        if (layer.Count == 0)
        {
            return null;
        }

        hash.AppendData(Encoding.UTF8.GetBytes(fileName));
        hash.AppendData([0]);
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(length, content.Length);
        hash.AppendData(length);
        hash.AppendData(content);
        return layer;
    }

    // An RFC 1123 label, the grammar of a Cohesion application name.
    private static bool IsOwner(string value)
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

    private sealed class DebugView
    {
        private readonly ResourceConfigurationRecord _target;

        public DebugView(ResourceConfigurationRecord value)
        {
            _target = value;
        }

        public string Digest => _target.Digest;

        public IReadOnlyList<string> ClaimOwners => _target.ClaimOwners;
    }
}
