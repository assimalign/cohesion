using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal sealed class DeclarativeResourceCommand : IResourceCommand
{
    private readonly byte[] _payload;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeclarativeResourceCommand"/> class.
    /// </summary>
    /// <param name="id">The deterministic SHA-256 identity of kind, target identity, and canonical payload.</param>
    /// <param name="kind">The command kind accepted by the target's manifest.</param>
    /// <param name="key">The nonblank, provider-scoped ownership conflict key.</param>
    /// <param name="target">The target resource instance registered in the declaring application's graph.</param>
    /// <param name="owner">The application that declares and owns this command.</param>
    /// <param name="payload">The canonical UTF-8 JSON payload; copied on construction.</param>
    /// <param name="optional">Whether rejection can be observed without blocking dependent resources.</param>
    public DeclarativeResourceCommand(
        string id,
        string kind,
        string key,
        IApplicationResource target,
        ApplicationName owner,
        ReadOnlyMemory<byte> payload,
        bool optional)
    {
        _payload = payload.ToArray();
        Id = id;
        Kind = kind;
        Key = key;
        Target = target;
        Owner = owner;
        Optional = optional;
    }

    public string Id { get; }
    public string Kind { get; }
    public string Key { get; }
    public IApplicationResource Target { get; }
    public ApplicationName Owner { get; }
    public ReadOnlyMemory<byte> Payload => _payload.AsSpan().ToArray();
    public bool Optional { get; }

    internal static byte[] Canonicalize(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = new List<JsonProperty>(value.EnumerateObject());
            properties.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            writer.WriteStartObject();
            foreach (JsonProperty property in properties)
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }

            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in value.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }

            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    internal static string CreateId(string kind, IApplicationResource target, ReadOnlySpan<byte> canonical)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes(kind));
        Append(hash, Encoding.UTF8.GetBytes(target is IManifestResource manifest
            ? manifest.Manifest.Application.ToString()
            : string.Empty));
        Append(hash, Encoding.UTF8.GetBytes(target.Name.ToString()));
        Append(hash, canonical);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
