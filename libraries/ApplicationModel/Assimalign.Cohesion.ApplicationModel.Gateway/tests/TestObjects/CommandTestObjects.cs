using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using HostCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class GatewayCommandJsonContext : JsonSerializerContext;

internal sealed class RecordingCommandClient : IGatewayResourceCommandClient
{
    private readonly List<string> _events;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingCommandClient"/> class.
    /// </summary>
    /// <param name="events">The shared list that records each command the client receives.</param>
    public RecordingCommandClient(List<string> events)
    {
        _events = events;
    }

    public RemoteCertificateValidationCallback? Validator { get; private set; }

    public string ResourceKind => "Database";
    public ResourceCommandResult Result { get; set; } = new(ResourceCommandStatus.Applied, "database created");
    public int ApplyCount { get; private set; }
    public Uri? Address { get; private set; }
    public string? Token { get; private set; }
    public HostCommand? Command { get; private set; }

    public ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, HostCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validator = serverCertificateValidator;
        _events.Add("apply:" + command.Key);
        ApplyCount++;
        Address = address;
        Token = bearerToken;
        Command = command;
        return ValueTask.FromResult(Result);
    }

    public ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, HostCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validator = serverCertificateValidator;
        _events.Add("remove:" + command.Key);
        return ValueTask.FromResult(new ResourceCommandResult(ResourceCommandStatus.Applied, "removed"));
    }
}

internal sealed class CommandController : IApplicationResourceController
{
    private readonly List<string> _events;
    private readonly string _scheme;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandController"/> class.
    /// </summary>
    /// <param name="events">The shared list that records each reconcile and delete call.</param>
    /// <param name="scheme">The scheme reported on the observed admin endpoint.</param>
    public CommandController(List<string> events, string scheme = "http")
    {
        _events = events;
        _scheme = scheme;
    }

    public bool CanRealize(ResourcePlan plan, out string? reason) { reason = null; return true; }

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("reconcile:" + context.Resource.Name);
        context.State.SetState(context.Resource.Id, ResourceLifecycle.Running, observedEndpoints:
            [new ResourceEndpoint("admin", _scheme, 12345, Host: "127.0.0.1")]);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        _events.Add("delete:" + context.Resource.Name);
        return Task.CompletedTask;
    }

    public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class RecordingCommandHandler : IResourceCommandHandler
{
    private readonly List<string> _events;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingCommandHandler"/> class.
    /// </summary>
    /// <param name="events">The shared list that records each command the handler executes.</param>
    public RecordingCommandHandler(List<string> events)
    {
        _events = events;
    }

    public string Kind => "database.add-database";
    public TimeSpan ApplyDelay { get; set; }

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(HostCommand command, CancellationToken cancellationToken = default)
    {
        await Task.Delay(ApplyDelay, cancellationToken).ConfigureAwait(false);
        _events.Add("direct:" + command.Owner);
        return ReadOnlyMemory<byte>.Empty;
    }
    public ValueTask<ReadOnlyMemory<byte>> DeleteAsync(HostCommand command, CancellationToken cancellationToken = default)
    {
        _events.Add("direct-remove:" + command.Owner);
        return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }
}
