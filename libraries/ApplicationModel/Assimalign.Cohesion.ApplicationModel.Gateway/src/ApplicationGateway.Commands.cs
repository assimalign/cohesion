using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private readonly Dictionary<(ApplicationName Application, ResourceName Target, string Id), ResourceCommand>
        _appliedCommands = new();

    /// <summary>Finds the running resource's registered control plane for direct in-process delivery.</summary>
    /// <param name="model">The application that realizes the target.</param>
    /// <param name="resource">The realized target.</param>
    /// <param name="controlPlane">The target's running control plane, when available.</param>
    /// <returns>True when direct delivery is available; otherwise the area client is used.</returns>
    protected virtual bool TryGetResourceControlPlane(
        IApplicationModel model, IApplicationResource resource,
        out IResourceControlPlane? controlPlane)
    {
        controlPlane = null;
        return false;
    }

    private async Task ApplyResourceCommandsAsync(ModelResource item, CancellationToken cancellationToken)
    {
        foreach (IResourceCommand command in item.Model.Commands)
        {
            if (!ReferenceEquals(command.Target, item.Descriptor.Resource))
            {
                continue;
            }

            ModelResource target = ResolveCommandTarget(item);
            TimeSpan targetBudget = GetReadinessBudget(target.Descriptor.Plan!);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(targetBudget);
            ResourceCommandResult result;
            try
            {
                ResourceManifest manifest = target.Model.Manifests[IndexOfDescriptor(target.Model.Descriptors, target.Descriptor)];
                bool alreadyApplied = _appliedCommands.ContainsKey((manifest.Application, manifest.Name, command.Id));
                ResourceLifecycle reached = alreadyApplied
                    ? ResourceLifecycle.Running
                    : await GetApplicationState(target.Model).WaitForStateAsync(
                        target.Descriptor.Resource.Id,
                        new HashSet<ResourceLifecycle> { ResourceLifecycle.Running, ResourceLifecycle.Failed, ResourceLifecycle.Stopped },
                        targetBudget, budget.Token).ConfigureAwait(false);
                result = reached == ResourceLifecycle.Running
                    ? await DeliverCommandAsync(item, command, delete: false, budget.Token).ConfigureAwait(false)
                    : new(ResourceCommandStatus.Rejected, $"Command target is '{reached}', not Running.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new(ResourceCommandStatus.Rejected, "Command delivery exceeded the target readiness budget.");
            }

            RecordCommand(item, command, result);
            if (result.Status == ResourceCommandStatus.Rejected && !command.Optional)
            {
                MarkDependentsBlocked(item);
                throw new InvalidOperationException(
                    $"Resource command '{command.Id}' ({command.Kind}) on '{command.Target.Name}' was rejected: {result.Detail}");
            }
        }
    }

    private async Task DeleteResourceCommandsAsync(ModelResource item, CancellationToken cancellationToken)
    {
        for (int index = item.Model.Commands.Count - 1; index >= 0; index--)
        {
            IResourceCommand command = item.Model.Commands[index];
            if (!ReferenceEquals(command.Target, item.Descriptor.Resource))
            {
                continue;
            }

            if (IsUnappliedOptionalCommand(item, command))
            {
                GetApplicationState(item.Model).RemoveCommandObservation(command.Target.Id, command.Owner.ToString(), command.Id);
                continue;
            }

            ResourceCommandResult result = await DeliverCommandAsync(item, command, delete: true, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == ResourceCommandStatus.Rejected)
            {
                RecordCommand(item, command, result);
                throw new InvalidOperationException($"Command teardown '{command.Id}' was rejected: {result.Detail}");
            }

            GetApplicationState(item.Model).RemoveCommandObservation(
                command.Target.Id, command.Owner.ToString(), command.Id);
        }
    }

    private void RecordCommand(ModelResource item, IResourceCommand command, ResourceCommandResult result) =>
        GetApplicationState(item.Model).SetCommandObservation(command.Target.Id, new ResourceCommandObservation(
            command.Target.Name.ToString(), command.Id, command.Kind, command.Owner.ToString(),
            command.Key, result.Status, result.Detail));

    private bool IsUnappliedOptionalCommand(ModelResource item, IResourceCommand command)
    {
        if (!command.Optional)
        {
            return false;
        }
        ModelResource target = ResolveCommandTarget(item);
        // A peer retains rejected observations. DELETE withdraws that record even when
        // no provider mutation was applied; local unsupported handlers have no such record.
        if (IsExternalPlan(target.Descriptor.Plan!))
        {
            return false;
        }
        ResourceManifest manifest = target.Model.Manifests[IndexOfDescriptor(target.Model.Descriptors, target.Descriptor)];
        if (_appliedCommands.TryGetValue((manifest.Application, manifest.Name, command.Id), out ResourceCommand? applied) &&
            applied.Owner == command.Owner.ToString())
        {
            return false;
        }
        foreach (ResourceCommandObservation observation in GetApplicationState(item.Model).GetCommandObservations(command.Target.Id))
        {
            if (observation.Id == command.Id && observation.Owner == command.Owner.ToString() &&
                observation.Status == ResourceCommandStatus.Rejected)
            {
                return true;
            }
        }
        return false;
    }

    private async Task RollBackResourceCommandsAsync(ModelResource item, CancellationToken cancellationToken)
    {
        ModelResource target = ResolveCommandTarget(item);
        ResourceManifest manifest = target.Model.Manifests[IndexOfDescriptor(target.Model.Descriptors, target.Descriptor)];
        for (int index = item.Model.Commands.Count - 1; index >= 0; index--)
        {
            IResourceCommand command = item.Model.Commands[index];
            if (!ReferenceEquals(command.Target, item.Descriptor.Resource) ||
                !_appliedCommands.TryGetValue((manifest.Application, manifest.Name, command.Id), out ResourceCommand? applied) ||
                applied.Owner != command.Owner.ToString())
            {
                continue;
            }
            ResourceCommandResult result = await DeliverCommandAsync(item, command, delete: true, cancellationToken).ConfigureAwait(false);
            if (result.Status == ResourceCommandStatus.Applied)
            {
                GetApplicationState(item.Model).RemoveCommandObservation(command.Target.Id, command.Owner.ToString(), command.Id);
            }
            else
            {
                RecordCommand(item, command, result);
            }
        }
    }

    private async ValueTask<ResourceCommandResult> DeliverCommandAsync(
        ModelResource item, IResourceCommand declaration, bool delete, CancellationToken cancellationToken)
    {
        var command = new ResourceCommand(declaration.Id, declaration.Kind, declaration.Owner.ToString(),
            declaration.Key, declaration.Payload);
        ModelResource target = ResolveCommandTarget(item);
        ResourceManifest manifest = target.Model.Manifests[IndexOfDescriptor(target.Model.Descriptors, target.Descriptor)];
        var key = (manifest.Application, manifest.Name, command.Id);

        if (declaration.Owner != item.Model.Name)
        {
            return new(ResourceCommandStatus.Rejected, "Command owner must equal the declaring application.");
        }

        if (_appliedCommands.TryGetValue(key, out ResourceCommand? prior))
        {
            if (!CommandContentMatches(prior, command))
            {
                return new(ResourceCommandStatus.Rejected,
                    $"Command id '{command.Id}' is already assigned to a different command or owner.");
            }
            if (!delete)
            {
                return new(ResourceCommandStatus.Applied, "The identical declaration is already applied.");
            }
        }

        foreach (var entry in _appliedCommands)
        {
            if (entry.Key.Application == key.Application && entry.Key.Target == key.Name &&
                entry.Value.Key == command.Key && entry.Value.Kind == command.Kind &&
                entry.Value.Owner != command.Owner)
            {
                return new(ResourceCommandStatus.Rejected,
                    $"Command key '{command.Key}' for kind '{command.Kind}' is owned by application '{entry.Value.Owner}'.");
            }
        }

        ResourceCommandResult result;
        try
        {
            if (IsExternalPlan(target.Descriptor.Plan!))
            {
                IExternalResourceResolver? resolver = (target.Descriptor.Resource as IExternalResource)?.Resolver;
                if (resolver is not IControlPlaneExternalResourceResolver { ControlPlaneAddress: { } remoteAddress } ||
                    _options.ControlPlaneClient is not IAuthenticatedControlPlaneClient peer)
                {
                    return new(ResourceCommandStatus.Rejected,
                        "Remote commands require an authenticated peer gateway control-plane binding.");
                }
                if (!CanSendCredential(item.Model, remoteAddress, out string? transportFailure))
                {
                    return new(ResourceCommandStatus.Rejected, transportFailure!);
                }
                string token = GetTrustState(item.Model.Name).Issue(
                    "cohesion-export", Name.ToString(), _options.DeveloperTokenLifetime,
                    _options.TimeProvider.GetUtcNow(), allowControlPlaneCommands: true);
                result = delete
                    ? await peer.DeleteCommandAsync(remoteAddress, manifest.Name, token, command, cancellationToken).ConfigureAwait(false)
                    : await peer.ApplyCommandAsync(remoteAddress, manifest.Name, token, command, cancellationToken).ConfigureAwait(false);
            }
            else if (TryGetResourceControlPlane(target.Model, target.Descriptor.Resource, out IResourceControlPlane? direct))
            {
                if (delete)
                {
                    await direct!.DeleteCommandAsync(command, cancellationToken).ConfigureAwait(false);
                    result = new(ResourceCommandStatus.Applied, "Owned declaration removed through the registered control plane.");
                }
                else
                {
                    ReadOnlyMemory<byte> response = await direct!.ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
                    result = new(ResourceCommandStatus.Applied, "Applied through the registered control plane.", response);
                }
            }
            else
            {
                IGatewayResourceCommandClient? client = null;
                foreach (IGatewayResourceCommandClient candidate in _options.CommandClients)
                {
                    if (candidate.ResourceKind == manifest.Kind)
                    {
                        client = candidate;
                        break;
                    }
                }
                if (client is null)
                {
                    return new(ResourceCommandStatus.Rejected, $"No command client is registered for resource kind '{manifest.Kind}'.");
                }

                Uri? address = null;
                foreach (ResourceEndpoint endpoint in GetApplicationState(target.Model).GetObservedEndpoints(target.Descriptor.Resource.Id))
                {
                    if (endpoint.Name == manifest.ControlPlane.Endpoint)
                    {
                        address = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port, manifest.ControlPlane.Path).Uri;
                        break;
                    }
                }
                if (address is null)
                {
                    return new(ResourceCommandStatus.Rejected, $"Target has no observed '{manifest.ControlPlane.Endpoint}' default control-plane endpoint.");
                }
                if (!CanSendCredential(item.Model, address, out string? transportFailure))
                {
                    return new(ResourceCommandStatus.Rejected, transportFailure!);
                }
                string token = ((IResourceCommandCredentialProvider)this).GetResourceCommandCredential(target.Model.Name, manifest.Name);
                result = delete
                    ? await client.DeleteAsync(address, token, command, cancellationToken).ConfigureAwait(false)
                    : await client.ApplyAsync(address, token, command, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ResourceCommandRejectedException exception)
        {
            result = new(ResourceCommandStatus.Rejected, exception.Detail);
        }
        catch (Exception exception) when (exception is NotSupportedException or HttpRequestException or IOException or InvalidOperationException or JsonException or FormatException)
        {
            // Transport and provider refusals are observations; cancellation still propagates.
            result = new(ResourceCommandStatus.Rejected, exception.Message);
        }

        if (result.Status == ResourceCommandStatus.Applied)
        {
            if (delete)
            {
                _appliedCommands.Remove(key);
            }
            else
            {
                _appliedCommands[key] = command;
            }
        }
        return result;
    }

    private ModelResource ResolveCommandTarget(ModelResource item)
    {
        if (item.Descriptor.Resource is not IExternalResource external || !IsExternalPlan(item.Descriptor.Plan!))
        {
            return item;
        }
        foreach (ModelResource candidate in _order)
        {
            if (candidate.Model.Name == external.Declaration.Application &&
                candidate.Descriptor.Resource.Name == external.Declaration.Name &&
                !IsExternalPlan(candidate.Descriptor.Plan!))
            {
                return candidate;
            }
        }
        return item;
    }

    private static bool CommandContentMatches(ResourceCommand left, ResourceCommand right) =>
        left.Id == right.Id && left.Kind == right.Kind && left.Owner == right.Owner &&
        left.Key == right.Key && left.Payload.Span.SequenceEqual(right.Payload.Span);

    private async Task<bool> TryReplaceCommandDeclarationsAsync(
        IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken)
    {
        if (_activeModels.Count != models.Count)
        {
            return false;
        }
        for (int index = 0; index < models.Count; index++)
        {
            IApplicationModel previous = _activeModels[index];
            IApplicationModel next = models[index];
            if (previous.Name != next.Name || previous.Resources.Count != next.Resources.Count)
            {
                return false;
            }
            for (int resource = 0; resource < previous.Resources.Count; resource++)
            {
                if (previous.Resources[resource].Id != next.Resources[resource].Id)
                {
                    return false;
                }
            }
            using var previousJson = ModelWithoutCommands(previous);
            using var nextJson = ModelWithoutCommands(next);
            if (!JsonElement.DeepEquals(previousJson.RootElement, nextJson.RootElement))
            {
                return false;
            }
        }

        // Resource realization is identical. Remove withdrawn declarations before replacing
        // their authoring snapshot; new declarations run through the normal admitted-target step.
        foreach (ModelResource item in _order)
        {
            IApplicationModel next = models[IndexOfApplication(models, item.Model.Name)];
            for (int index = item.Model.Commands.Count - 1; index >= 0; index--)
            {
                IResourceCommand command = item.Model.Commands[index];
                if (!ReferenceEquals(command.Target, item.Descriptor.Resource))
                {
                    continue;
                }
                bool retained = false;
                foreach (IResourceCommand candidate in next.Commands)
                {
                    retained |= candidate.Id == command.Id && candidate.Owner == command.Owner &&
                        candidate.Target.Name == command.Target.Name && candidate.Key == command.Key;
                }
                if (retained)
                {
                    continue;
                }
                if (IsUnappliedOptionalCommand(item, command))
                {
                    GetApplicationState(item.Model).RemoveCommandObservation(command.Target.Id, command.Owner.ToString(), command.Id);
                    continue;
                }
                ResourceCommandResult result = await DeliverCommandAsync(item, command, delete: true, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status == ResourceCommandStatus.Rejected)
                {
                    RecordCommand(item, command, result);
                    throw new InvalidOperationException($"Withdrawn command '{command.Id}' could not be removed: {result.Detail}");
                }
                GetApplicationState(item.Model).RemoveCommandObservation(command.Target.Id, command.Owner.ToString(), command.Id);
            }
        }

        var order = new List<ModelResource>();
        var states = new ApplicationStateView[models.Count];
        var snapshot = new IApplicationModel[models.Count];
        for (int index = 0; index < models.Count; index++)
        {
            snapshot[index] = models[index];
            states[index] = _applicationStates[index] with { Model = models[index] };
            foreach (IApplicationResourceDescriptor descriptor in OrderTopologically(models[index]))
            {
                order.Add(new ModelResource(models[index], descriptor));
            }
        }
        _activeModels = snapshot;
        _applicationStates = states;
        _order = order;
        return true;
    }

    private static int IndexOfApplication(IReadOnlyList<IApplicationModel> models, ApplicationName name)
    {
        for (int index = 0; index < models.Count; index++)
        {
            if (models[index].Name == name)
            {
                return index;
            }
        }
        throw new InvalidOperationException($"Application '{name}' is not in the replacement model.");
    }

    private static JsonDocument ModelWithoutCommands(IApplicationModel model)
    {
        using var stream = new MemoryStream();
        ApplicationModelDocument.Create(model).Save(stream);
        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "commands")
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(output.ToArray());
    }
}
