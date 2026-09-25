using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal static class ResourceCommandValidator
{
    internal static void Validate(
        IReadOnlyList<IResourceCommand> commands,
        ApplicationName owner,
        IReadOnlyList<IApplicationResourceDescriptor> descriptors,
        IReadOnlyList<ResourceManifest> manifests)
    {
        var targets = new Dictionary<IApplicationResource, ResourceManifest>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < descriptors.Count; index++)
        {
            targets.Add(descriptors[index].Resource, manifests[index]);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targetKeys = new Dictionary<IApplicationResource, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (IResourceCommand command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                throw new InvalidOperationException($"Command '{command.Kind}' requires a nonblank ownership key.");
            }

            if (command.Owner != owner)
            {
                throw new InvalidOperationException($"Command '{command.Kind}' is owned by '{command.Owner}', not declaring application '{owner}'.");
            }

            if (!targets.TryGetValue(command.Target, out ResourceManifest? manifest))
            {
                throw new InvalidOperationException($"Command '{command.Kind}' targets resource '{command.Target.Name}', which is not owned or referenced by this graph.");
            }

            bool accepted = false;
            foreach (ResourceManifestCommand acceptedCommand in manifest.Commands)
            {
                accepted |= string.Equals(acceptedCommand.Kind, command.Kind, StringComparison.Ordinal);
            }

            if (!accepted)
            {
                throw new InvalidOperationException($"Resource '{manifest.Name}' does not accept command kind '{command.Kind}'; declare a kind advertised by its manifest.");
            }

            if (string.IsNullOrWhiteSpace(command.Id) || !ids.Add(command.Id))
            {
                throw new InvalidOperationException($"Command '{command.Kind}' has a blank or duplicate command id '{command.Id}'.");
            }

            if (!targetKeys.TryGetValue(command.Target, out HashSet<string>? keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                targetKeys.Add(command.Target, keys);
            }
            if (!keys.Add(command.Key))
            {
                throw new InvalidOperationException($"Resource '{manifest.Name}' has conflicting desired commands for ownership key '{command.Key}'; declare one command per target key.");
            }

            try
            {
                byte[] canonical = DeclarativeResourceCommand.Canonicalize(command.Payload);
                if (!canonical.AsSpan().SequenceEqual(command.Payload.Span)
                    || command.Id != DeclarativeResourceCommand.CreateId(command.Kind, command.Target, canonical))
                {
                    throw new InvalidOperationException($"Command '{command.Id}' has a noncanonical payload or mismatched deterministic identity.");
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"Command '{command.Id}' has an invalid JSON payload.", exception);
            }
        }
    }
}
