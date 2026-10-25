using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed partial class ResourceManagementCoordinator
{
    private async ValueTask AdmitConfiguredBaselinesAsync(ApplicationName application, ResourceName resource,
        ResourceInstanceInventory inventory,
        IReadOnlyList<(ResourceManagementTarget Target, ResourceRuntimeDescription Description)> descriptions,
        CancellationToken cancellationToken)
    {
        (ResourceManagementState state, _) = await ReadAsync(application, resource, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(descriptions.Select(description => AdmitAsync(description.Target, description.Description))).ConfigureAwait(false);

        async Task AdmitAsync(ResourceManagementTarget target, ResourceRuntimeDescription description)
        {
            var required = new List<ResourceBootstrapState>();
            var slots = new HashSet<(string Name, string Key)>();
            if (state.Incarnation == inventory.Incarnation)
            {
                // Never omit an accepted slot because this process does not advertise its contract.
                // The resource must verify every durable revision before it can admit traffic.
                foreach (ResourceManagementSlot slot in state.Slots.Values)
                {
                    required.Add(new ResourceBootstrapState(slot.Address.State, slot.Address.Key, slot.Revision, slot.ValueDigest));
                    slots.Add((slot.Address.State.Value, slot.Address.Key.Value));
                }
            }
            if (description.Manifest.TryGetProperty("initialState", out JsonElement initial))
            {
                foreach (JsonElement declaration in initial.EnumerateArray())
                {
                    string name = declaration.GetProperty("name").GetString()!;
                    string key = declaration.GetProperty("key").GetString()!;
                    if (!slots.Add((name, key))) { continue; }
                    JsonElement desired = declaration.GetProperty("desired");
                    required.Add(new ResourceBootstrapState(name, key, 0,
                        ResourceStateWire.ComputeValueDigest(desired.GetProperty("exists").GetBoolean(), desired.GetProperty("value"))));
                }
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _protocol.AdmitAsync(target, application, resource, description.ProcessStartId, required, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or System.Net.Http.HttpRequestException or OperationCanceledException or
                InvalidOperationException or JsonException or NotSupportedException)
            {
                // Admission is best effort. Incompatible or unapplied durable state keeps this process closed.
            }
        }
    }

    private static string ComputeCatalogDigest(JsonElement catalog)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in catalog.EnumerateObject())
            {
                if (property.Name == "initialState") { continue; }
                writer.WritePropertyName(property.Name);
                if (property.Name == "states")
                {
                    writer.WriteStartArray();
                    foreach (JsonElement state in property.Value.EnumerateArray()
                        .OrderBy(static state => state.GetProperty("name").GetString(), StringComparer.Ordinal)) { state.WriteTo(writer); }
                    writer.WriteEndArray();
                }
                else { property.Value.WriteTo(writer); }
            }
            writer.WriteStartArray("initialState");
            if (catalog.TryGetProperty("initialState", out JsonElement initial))
            {
                foreach (JsonElement declaration in initial.EnumerateArray()
                    .OrderBy(static state => state.GetProperty("name").GetString(), StringComparer.Ordinal)
                    .ThenBy(static state => state.GetProperty("key").GetString(), StringComparer.Ordinal)) { declaration.WriteTo(writer); }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using JsonDocument normalized = JsonDocument.Parse(buffer.WrittenMemory);
        return ResourceStateWire.ComputeDigest(normalized.RootElement);
    }
}
