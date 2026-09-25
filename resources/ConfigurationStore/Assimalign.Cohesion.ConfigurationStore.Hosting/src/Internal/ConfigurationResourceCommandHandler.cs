using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

internal sealed class ConfigurationResourceCommandHandler : IResourceCommandHandler
{
    private readonly ConfigurationStoreRepository _repository;

    internal ConfigurationResourceCommandHandler(string kind, ConfigurationStoreRepository repository)
    {
        Kind = kind;
        _repository = repository;
    }

    public string Kind { get; }

    public ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        MutateAsync(command, delete: false, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        MutateAsync(command, delete: true, cancellationToken);

    private async ValueTask<ReadOnlyMemory<byte>> MutateAsync(ResourceCommand command, bool delete, CancellationToken cancellationToken)
    {
        int separator = command.Key.LastIndexOf('/');
        string namespaceName = separator > 0 ? command.Key[..separator] : string.Empty;
        string key = separator >= 0 && separator < command.Key.Length - 1 ? command.Key[(separator + 1)..] : string.Empty;
        string? value = null;
        if (!command.Payload.IsEmpty)
        {
            using JsonDocument document = JsonDocument.Parse(command.Payload);
            JsonElement payload = document.RootElement;
            if (payload.ValueKind is not JsonValueKind.Object)
            {
                throw new JsonException("A configuration command payload must be a JSON object.");
            }
            bool hasNamespace = payload.TryGetProperty("namespace", out JsonElement ns);
            bool hasKey = payload.TryGetProperty("key", out JsonElement target);
            if (hasNamespace || hasKey)
            {
                if (!hasNamespace || !hasKey || ns.ValueKind is not JsonValueKind.String || target.ValueKind is not JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(ns.GetString()) || string.IsNullOrWhiteSpace(target.GetString()) ||
                    ns.GetString() + "/" + target.GetString() != command.Key)
                {
                    throw new ResourceCommandRejectedException($"{Kind} payload namespace/key must match command key '{command.Key}'.");
                }
                namespaceName = ns.GetString()!;
                key = target.GetString()!;
            }
            if (!delete && Kind == ConfigurationEndpointService.SetValueCommand)
            {
                if (!payload.TryGetProperty("value", out JsonElement item) || item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new JsonException("A configurationstore.set-value payload requires a string or null 'value'.");
                }
                value = item.ValueKind is JsonValueKind.Null ? null : item.GetString();
            }
        }
        else if (!delete && Kind == ConfigurationEndpointService.SetValueCommand)
        {
            throw new JsonException("A configurationstore.set-value payload requires 'value'.");
        }
        if (string.IsNullOrWhiteSpace(namespaceName) || string.IsNullOrWhiteSpace(key))
        {
            throw new JsonException("Configuration command keys must supply a nonblank namespace and key.");
        }
        if (key.Contains('/'))
        {
            throw new ResourceCommandRejectedException($"{Kind} configuration key '{key}' must not contain '/'; the final slash separates the namespace from the key.");
        }

        // Removing a remove-value declaration releases its ownership; it does not restore data
        // whose old value was not part of the declarative contract.
        bool found = delete && Kind == ConfigurationEndpointService.RemoveValueCommand
            ? await _repository.ReadAsync(namespaceName, cancellationToken).ConfigureAwait(false) is not null
            : !delete && Kind == ConfigurationEndpointService.SetValueCommand
                ? await _repository.SetAsync(namespaceName, key, value, cancellationToken).ConfigureAwait(false)
                : await _repository.RemoveAsync(namespaceName, key, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            throw new ConfigurationNamespaceNotFoundException($"{Kind} cannot find configuration namespace '{namespaceName}'. Declare the namespace in the target resource first.");
        }
        return ReadOnlyMemory<byte>.Empty;
    }
}
