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

namespace Assimalign.Cohesion.Rezolvr.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("RezolvrRecordRepository")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class RezolvrRecordRepository
{
    internal const string AddARecord = "rezolvr.add-a-record";
    internal const string AddCnameRecord = "rezolvr.add-cname-record";

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, StoredRezolvrRecord>? _records;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezolvrRecordRepository"/> class.
    /// </summary>
    /// <param name="dataPath">The directory that holds the persisted <c>records.json</c> document.</param>
    public RezolvrRecordRepository(string dataPath)
    {
        _path = Path.Combine(Path.GetFullPath(dataPath), "records.json");
    }

    internal async Task<IReadOnlyList<RezolvrRecordCommand>> ReadCommandsAsync(CancellationToken cancellationToken = default)
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

    internal async Task MutateAsync(RezolvrRecordCommand command, bool delete, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        JsonElement payload = document.RootElement;
        string name = Required(payload, "name");
        bool addressRecord = command.Kind == AddARecord;
        string value = Required(payload, addressRecord ? "address" : "target");
        if (name != command.Key || name.Contains('/') || Uri.CheckHostName(name.TrimEnd('.')) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException($"{command.Kind} requires a DNS name matching key '{command.Key}'.");
        }
        if (!payload.TryGetProperty("ttlSeconds", out JsonElement ttl) || !ttl.TryGetInt32(out int seconds) || seconds <= 0)
        {
            throw new InvalidOperationException($"{command.Kind} record '{name}' requires a positive ttlSeconds.");
        }
        if (addressRecord)
        {
            if (!IPAddress.TryParse(value, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException($"{command.Kind} record '{name}' requires an IPv4 address.");
            }
            value = address.ToString();
        }
        else if (value.Contains('/') || Uri.CheckHostName(value.TrimEnd('.')) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException($"{command.Kind} record '{name}' requires a DNS target.");
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
                    throw new InvalidOperationException($"{command.Kind} record '{name}' belongs to owner '{existing.Owner}'; owner '{command.Owner}' cannot overwrite or delete it.");
                }
                if (existing.Kind != command.Kind || existing.Value != value || existing.TtlSeconds != seconds)
                {
                    throw new InvalidOperationException($"{command.Kind} record '{name}' conflicts with its stored declaration; delete that declaration before changing the record.");
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

    private sealed class DebugView
    {
        public DebugView(RezolvrRecordRepository value)
        {
        }
    }
}

// One record declaration as the repository receives and replays it: the idempotency id, the record
// kind (AddARecord or AddCnameRecord), the owning application, the record-name key, and the JSON payload.
internal sealed record RezolvrRecordCommand(string Id, string Kind, string Owner, string Key, ReadOnlyMemory<byte> Payload);

[global::System.Diagnostics.DebuggerDisplay("StoredRezolvrRecord {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed record StoredRezolvrRecord(string Id, string Kind, string Owner, string Key, byte[] Payload, string Value, int TtlSeconds)
{
    [JsonIgnore]
    internal RezolvrRecordCommand Command => new(Id, Kind, Owner, Key, Payload);

    private sealed class DebugView
    {
        private readonly StoredRezolvrRecord _target;

        public DebugView(StoredRezolvrRecord value)
        {
            _target = value;
        }

        public string Id => _target.Id;
        public string Kind => _target.Kind;
        public string Owner => _target.Owner;
        public string Key => _target.Key;
        public byte[] Payload => _target.Payload;
        public string Value => _target.Value;
        public int TtlSeconds => _target.TtlSeconds;
    }
}

[global::System.Diagnostics.DebuggerDisplay("RezolvrRecordJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredRezolvrRecord[]))]
internal sealed partial class RezolvrRecordJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly RezolvrRecordJsonContext _target;

        public DebugView(RezolvrRecordJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}
