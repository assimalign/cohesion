using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class DeclarativeResourceCommand(
    string id,
    string kind,
    string key,
    IApplicationResource target,
    ApplicationName owner,
    ReadOnlyMemory<byte> payload,
    bool optional) : IResourceCommand
{
    private readonly byte[] _payload = payload.ToArray();

    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string Key { get; } = key;
    public IApplicationResource Target { get; } = target;
    public ApplicationName Owner { get; } = owner;
    public ReadOnlyMemory<byte> Payload => _payload.AsSpan().ToArray();
    public bool Optional { get; } = optional;

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
