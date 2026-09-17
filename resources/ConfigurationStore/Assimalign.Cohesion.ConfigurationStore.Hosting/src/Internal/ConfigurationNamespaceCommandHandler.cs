using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class ConfigurationNamespaceCommandHandler(ConfigurationStoreRepository repository) : IResourceCommandHandler
{
    public string Kind => ConfigurationEndpointService.AddNamespaceCommand;

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        JsonElement payload = document.RootElement;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Contains('/') ||
            name.GetString() != command.Key)
        {
            throw new ResourceCommandRejectedException($"{Kind} requires a single-segment name matching key '{command.Key}'.");
        }
        var seed = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (payload.TryGetProperty("seed", out JsonElement values) && values.ValueKind != JsonValueKind.Null)
        {
            if (values.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A namespace seed must be a string-or-null JSON object.");
            }
            foreach (JsonProperty property in values.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Contains('/') ||
                    property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                    !seed.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString()))
                {
                    throw new JsonException("A namespace seed requires unique nonblank keys without '/' and string-or-null values.");
                }
            }
        }
        await repository.CreateNamespaceAsync(command.Key, command.Owner, seed, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await repository.DeleteNamespaceAsync(command.Key, command.Owner, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }
}
