using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using HostCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class GatewayCommandJsonContext : JsonSerializerContext;

internal sealed class RecordingCommandClient(List<string> events) : IGatewayResourceCommandClient
{
    public string ResourceKind => "Database";
    public ResourceCommandResult Result { get; set; } = new(ResourceCommandStatus.Applied, "database created");
    public int ApplyCount { get; private set; }
    public Uri? Address { get; private set; }
    public string? Token { get; private set; }
    public HostCommand? Command { get; private set; }

    public ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, HostCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Add("apply:" + command.Key);
        ApplyCount++;
        Address = address;
        Token = bearerToken;
        Command = command;
        return ValueTask.FromResult(Result);
    }

    public ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, HostCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Add("remove:" + command.Key);
        return ValueTask.FromResult(new ResourceCommandResult(ResourceCommandStatus.Applied, "removed"));
    }
}

internal sealed class CommandController(List<string> events) : IApplicationResourceController
{
    public bool CanRealize(ResourcePlan plan, out string? reason) { reason = null; return true; }

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Add("reconcile:" + context.Resource.Name);
        context.State.SetState(context.Resource.Id, ResourceLifecycle.Running, observedEndpoints:
            [new ResourceEndpoint("admin", "http", 12345, Host: "127.0.0.1")]);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        events.Add("delete:" + context.Resource.Name);
        return Task.CompletedTask;
    }

    public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class RecordingCommandHandler(List<string> events) : IResourceCommandHandler
{
    public string Kind => "database.add-database";
    public TimeSpan ApplyDelay { get; set; }

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(HostCommand command, CancellationToken cancellationToken = default)
    {
        await Task.Delay(ApplyDelay, cancellationToken).ConfigureAwait(false);
        events.Add("direct:" + command.Owner);
        return ReadOnlyMemory<byte>.Empty;
    }
    public ValueTask<ReadOnlyMemory<byte>> DeleteAsync(HostCommand command, CancellationToken cancellationToken = default)
    {
        events.Add("direct-remove:" + command.Owner);
        return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }
}
