using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

internal sealed class RezolvrRecordRepository(string dataPath)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(dataPath), "records.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, StoredRezolvrRecord>? _records;

    internal async Task<IReadOnlyList<ResourceCommand>> ReadCommandsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            return _records!.Values.Select(static value => value.Command).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task MutateAsync(ResourceCommand command, bool delete, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        JsonElement payload = document.RootElement;
        string name = Required(payload, "name");
        bool addressRecord = command.Kind == RezolvrResourceCommandHandler.AddARecord;
        string value = Required(payload, addressRecord ? "address" : "target");
        if (name != command.Key || name.Contains('/') || Uri.CheckHostName(name.TrimEnd('.')) != UriHostNameType.Dns)
        {
            throw new ResourceCommandRejectedException($"{command.Kind} requires a DNS name matching key '{command.Key}'.");
        }
        if (!payload.TryGetProperty("ttlSeconds", out JsonElement ttl) || !ttl.TryGetInt32(out int seconds) || seconds <= 0)
        {
            throw new ResourceCommandRejectedException($"{command.Kind} record '{name}' requires a positive ttlSeconds.");
        }
        if (addressRecord)
        {
            if (!IPAddress.TryParse(value, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ResourceCommandRejectedException($"{command.Kind} record '{name}' requires an IPv4 address.");
            }
            value = address.ToString();
        }
        else if (value.Contains('/') || Uri.CheckHostName(value.TrimEnd('.')) != UriHostNameType.Dns)
        {
            throw new ResourceCommandRejectedException($"{command.Kind} record '{name}' requires a DNS target.");
        }

        string key = name.TrimEnd('.');
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (_records!.TryGetValue(key, out StoredRezolvrRecord? existing))
            {
                if (existing.Owner != command.Owner)
                {
                    throw new ResourceCommandRejectedException($"{command.Kind} record '{name}' belongs to owner '{existing.Owner}'; owner '{command.Owner}' cannot overwrite or delete it.");
                }
                if (existing.Kind != command.Kind || existing.Value != value || existing.TtlSeconds != seconds)
                {
                    throw new ResourceCommandRejectedException($"{command.Kind} record '{name}' conflicts with its stored declaration; delete that declaration before changing the record.");
                }
            }
            else if (delete)
            {
                return;
            }
            var updated = new Dictionary<string, StoredRezolvrRecord>(_records, StringComparer.OrdinalIgnoreCase);
            if (delete)
            {
                updated.Remove(key);
            }
            else
            {
                updated[key] = new StoredRezolvrRecord(command.Id, command.Kind, command.Owner, command.Key,
                    command.Payload.ToArray(), value, seconds);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                    updated.Values.OrderBy(static record => record.Key, StringComparer.Ordinal).ToArray(),
                    RezolvrRecordJsonContext.Default.StoredRezolvrRecordArray);
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, _path, overwrite: true);
                _records = updated;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_records is not null)
        {
            return;
        }
        StoredRezolvrRecord[] stored = File.Exists(_path)
            ? JsonSerializer.Deserialize(await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false),
                RezolvrRecordJsonContext.Default.StoredRezolvrRecordArray)
                ?? throw new InvalidDataException("The Rezolvr records document cannot be null.")
            : [];
        _records = stored.ToDictionary(static value => value.Key.TrimEnd('.'), StringComparer.OrdinalIgnoreCase);
    }

    private static string Required(JsonElement payload, string property)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"A Rezolvr record payload requires a nonblank '{property}'.");
        }
        return value.GetString()!;
    }
}

internal sealed record StoredRezolvrRecord(string Id, string Kind, string Owner, string Key, byte[] Payload, string Value, int TtlSeconds)
{
    [JsonIgnore]
    internal ResourceCommand Command => new(Id, Kind, Owner, Key, Payload);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredRezolvrRecord[]))]
internal sealed partial class RezolvrRecordJsonContext : JsonSerializerContext;
