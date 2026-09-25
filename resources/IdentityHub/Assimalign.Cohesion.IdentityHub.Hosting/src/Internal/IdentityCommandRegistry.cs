using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Internal;

internal sealed class IdentityCommandRegistry
{
    private readonly string _path;
    private readonly ResourceContext _context;
    private readonly HashSet<string> _seedAudiences;
    private readonly Dictionary<string, IdentityHubClientRegistration> _seedClients;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<(string Kind, string Key), StoredIdentityCommand> _commands = new();
    private Dictionary<string, IdentityHubClientRegistration> _clients;
    private HashSet<string> _audiences;

    internal IdentityCommandRegistry(string dataPath, ResourceContext context,
        IReadOnlyCollection<string> audiences, IReadOnlyDictionary<string, IdentityHubClientRegistration> clients)
    {
        _path = Path.Combine(Path.GetFullPath(dataPath), "registry.json");
        _context = context;
        _seedAudiences = new HashSet<string>(audiences, StringComparer.Ordinal);
        _seedClients = new Dictionary<string, IdentityHubClientRegistration>(clients, StringComparer.Ordinal);
        _audiences = new HashSet<string>(_seedAudiences, StringComparer.Ordinal);
        _clients = new Dictionary<string, IdentityHubClientRegistration>(_seedClients, StringComparer.Ordinal);
    }

    internal bool TryGetClient(string id, [NotNullWhen(true)] out IdentityHubClientRegistration? client)
    {
        lock (_sync)
        {
            return _clients.TryGetValue(id, out client);
        }
    }

    internal async Task<IReadOnlyList<ResourceCommand>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredIdentityCommand[] stored = File.Exists(_path)
                ? JsonSerializer.Deserialize(await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false),
                    IdentityRegistryJsonContext.Default.StoredIdentityCommandArray)
                    ?? throw new InvalidDataException("The IdentityHub registry document cannot be null.")
                : [];
            Dictionary<(string Kind, string Key), StoredIdentityCommand> commands = stored.ToDictionary(static value => (value.Kind, value.Key));
            (HashSet<string> audiences, Dictionary<string, IdentityHubClientRegistration> clients) = BuildSnapshot(commands);
            lock (_sync)
            {
                _commands = commands;
                _audiences = audiences;
                _clients = clients;
            }
            return stored.OrderBy(static command => command.Kind, StringComparer.Ordinal).Select(static command => command.Command).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task MutateAsync(ResourceCommand command, bool delete, CancellationToken cancellationToken = default)
    {
        StoredIdentityCommand proposed = Parse(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_commands.TryGetValue((command.Kind, command.Key), out StoredIdentityCommand? existing))
            {
                if (existing.Owner != command.Owner)
                {
                    throw new ResourceCommandRejectedException($"{command.Kind} key '{command.Key}' belongs to owner '{existing.Owner}'; owner '{command.Owner}' cannot overwrite or delete it.");
                }
                if (existing.Kind != proposed.Kind || existing.CredentialSource != proposed.CredentialSource ||
                    !existing.Audiences.SequenceEqual(proposed.Audiences, StringComparer.Ordinal))
                {
                    throw new ResourceCommandRejectedException($"{command.Kind} key '{command.Key}' conflicts with its stored declaration; delete it before changing the registration.");
                }
            }
            else if (delete)
            {
                return;
            }
            var updated = new Dictionary<(string Kind, string Key), StoredIdentityCommand>(_commands);
            if (delete) { updated.Remove((command.Kind, command.Key)); }
            else { updated[(command.Kind, command.Key)] = proposed; }
            (HashSet<string> audiences, Dictionary<string, IdentityHubClientRegistration> clients) = BuildSnapshot(updated);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                    updated.Values.OrderBy(static entry => entry.Key, StringComparer.Ordinal).ToArray(),
                    IdentityRegistryJsonContext.Default.StoredIdentityCommandArray);
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                File.Move(temporary, _path, overwrite: true);
                lock (_sync)
                {
                    _commands = updated;
                    _audiences = audiences;
                    _clients = clients;
                }
            }
            finally
            {
                if (File.Exists(temporary)) { File.Delete(temporary); }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private (HashSet<string> Audiences, Dictionary<string, IdentityHubClientRegistration> Clients) BuildSnapshot(
        Dictionary<(string Kind, string Key), StoredIdentityCommand> commands)
    {
        var audiences = new HashSet<string>(_seedAudiences, StringComparer.Ordinal);
        var clients = new Dictionary<string, IdentityHubClientRegistration>(_seedClients, StringComparer.Ordinal);
        foreach (StoredIdentityCommand command in commands.Values.Where(static value => value.Kind == IdentityResourceCommandHandler.AddAudience))
        {
            if (!audiences.Add(command.Key))
            {
                throw new ResourceCommandRejectedException($"identityhub.add-audience cannot claim audience '{command.Key}' declared by the resource.");
            }
        }
        foreach (StoredIdentityCommand command in commands.Values.Where(static value => value.Kind == IdentityResourceCommandHandler.AddClient))
        {
            if (clients.ContainsKey(command.Key))
            {
                throw new ResourceCommandRejectedException($"identityhub.add-client cannot claim client '{command.Key}' declared by the resource.");
            }
            foreach (string audience in command.Audiences)
            {
                if (!audiences.Contains(audience))
                {
                    throw new ResourceCommandRejectedException($"identityhub.add-client client '{command.Key}' requires audience '{audience}'; declare it before the client and remove the client before its audience.");
                }
            }
            string source = command.CredentialSource!;
            string mountName = source.StartsWith("mount:", StringComparison.Ordinal) ? source["mount:".Length..] : source;
            if (!_context.Mounts.TryGetValue(mountName, out ResourceMount? mount))
            {
                throw new ResourceCommandRejectedException($"identityhub.add-client client '{command.Key}' cannot resolve credential source '{source}'; provide that named Secret mount to the IdentityHub resource.");
            }
            byte[] secret = mount.ReadAllBytes();
            try
            {
                if (secret.Length == 0)
                {
                    throw new ResourceCommandRejectedException($"identityhub.add-client client '{command.Key}' credential source '{source}' is empty.");
                }
                clients.Add(command.Key, IdentityHubClientRegistration.CreateCommand(command.Key, command.Audiences, secret));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
        return (audiences, clients);
    }

    private static StoredIdentityCommand Parse(ResourceCommand command)
    {
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        JsonElement payload = document.RootElement;
        string name = Required(payload, command.Kind == IdentityResourceCommandHandler.AddAudience ? "name" : "clientId");
        if (name != command.Key || name.Contains('/'))
        {
            throw new ResourceCommandRejectedException($"{command.Kind} requires a single-segment identifier matching key '{command.Key}'.");
        }
        string[] audiences = [];
        string? source = null;
        if (command.Kind == IdentityResourceCommandHandler.AddClient)
        {
            source = Required(payload, "credentialSource");
            if (source.StartsWith("literal:", StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceCommandRejectedException("identityhub.add-client forbids literal credential sources; provide a named Secret mount.");
            }
            if (!payload.TryGetProperty("audiences", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("identityhub.add-client requires an audiences array.");
            }
            audiences = values.EnumerateArray().Select(static value =>
            {
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Contains('/'))
                {
                    throw new JsonException("Client audiences must be nonblank identifiers without '/'.");
                }
                return value.GetString()!;
            }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (audiences.Length == 0) { throw new JsonException("A client must name at least one audience."); }
        }
        return new StoredIdentityCommand(command.Id, command.Kind, command.Owner, command.Key, command.Payload.ToArray(), audiences, source);
    }

    private static string Required(JsonElement payload, string property)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"An identity command payload requires a nonblank '{property}'.");
        }
        return value.GetString()!;
    }
}

internal sealed record StoredIdentityCommand(string Id, string Kind, string Owner, string Key, byte[] Payload, string[] Audiences, string? CredentialSource)
{
    [JsonIgnore]
    internal ResourceCommand Command => new(Id, Kind, Owner, Key, Payload);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredIdentityCommand[]))]
internal sealed partial class IdentityRegistryJsonContext : JsonSerializerContext;
