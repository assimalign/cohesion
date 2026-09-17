using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Hosting.Resources;

internal sealed class DefaultResourceControlPlane : IResourceControlPlane
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<string> _acceptedCommandKinds;
    private readonly Dictionary<string, IHealthContributor> _contributors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Uri> _observedEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IResourceCommandHandler> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ResourceCommand Command, ReadOnlyMemory<byte> Response)> _commands = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private IHost? _host;

    internal DefaultResourceControlPlane(IEnumerable<string> acceptedCommandKinds)
    {
        ArgumentNullException.ThrowIfNull(acceptedCommandKinds);

        _acceptedCommandKinds = acceptedCommandKinds
            .Select(static kind =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(kind);
                return kind;
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> AcceptedCommandKinds => _acceptedCommandKinds;

    public IReadOnlyList<ResourceCommand> Commands
    {
        get
        {
            lock (_sync)
            {
                return _commands.Values.Select(static entry => entry.Command with { Payload = entry.Command.Payload.ToArray() }).ToArray();
            }
        }
    }

    public void RegisterCommandHandler(IResourceCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(handler.Kind);
        if (!_acceptedCommandKinds.Contains(handler.Kind, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Resource command kind '{handler.Kind}' is not advertised by this control plane.", nameof(handler));
        }

        lock (_sync)
        {
            if (!_handlers.TryAdd(handler.Kind, handler))
            {
                throw new InvalidOperationException($"Resource command handler '{handler.Kind}' is already registered.");
            }
        }
    }

    public IReadOnlyDictionary<string, Uri> ObservedEndpoints
    {
        get
        {
            lock (_sync)
            {
                return new ReadOnlyDictionary<string, Uri>(
                    new Dictionary<string, Uri>(_observedEndpoints, StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    public void AddHealthContributor(IHealthContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        ArgumentException.ThrowIfNullOrWhiteSpace(contributor.Name);

        lock (_sync)
        {
            if (!_contributors.TryAdd(contributor.Name, contributor))
            {
                throw new InvalidOperationException(
                    $"A resource health contributor named '{contributor.Name}' is already registered.");
            }
        }
    }

    public void ObserveEndpoint(string name, Uri address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Uri.ThrowIfNotEndpoint(address);

        lock (_sync)
        {
            _observedEndpoints[name] = address;
        }
    }

    public ValueTask<ResourceHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default)
        => CheckContributorsAsync(cancellationToken);

    public ValueTask<ResourceHealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default)
        => CheckContributorsAsync(cancellationToken);

    public ValueTask<ResourceHealthReport> CheckLivenessAsync(CancellationToken cancellationToken = default)
        => CheckContributorsAsync(cancellationToken);

    public void AttachHost(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        lock (_sync)
        {
            if (_host is not null && !ReferenceEquals(_host, host))
            {
                throw new InvalidOperationException("The resource control plane is already attached to a host.");
            }

            _host = host;
        }
    }

    public ValueTask RequestStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHost host;
        lock (_sync)
        {
            host = _host
                ?? throw new InvalidOperationException("The resource control plane is not attached to a host.");
        }

        host.Context.Shutdown();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default)
        => DispatchCommandAsync(command, delete: false, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>> DeleteCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default)
        => DispatchCommandAsync(command, delete: true, cancellationToken);

    private async ValueTask<ReadOnlyMemory<byte>> DispatchCommandAsync(
        ResourceCommand command,
        bool delete,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Key);
        command = command with { Payload = command.Payload.ToArray() };

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IResourceCommandHandler handler;
            (ResourceCommand Command, ReadOnlyMemory<byte> Response) existing;
            bool found;
            lock (_sync)
            {
                if (!_handlers.TryGetValue(command.Kind, out handler!))
                {
                    throw new NotSupportedException($"Resource command kind '{command.Kind}' is not implemented by this control plane.");
                }
                found = _commands.TryGetValue(command.Key, out existing);
                if (found && !string.Equals(existing.Command.Owner, command.Owner, StringComparison.Ordinal))
                {
                    throw new ResourceCommandRejectedException(
                        $"Command '{command.Kind}' key '{command.Key}' belongs to owner '{existing.Command.Owner}'; owner '{command.Owner}' cannot overwrite or delete it.");
                }
                if (found && delete && (existing.Command.Id != command.Id || existing.Command.Kind != command.Kind))
                {
                    throw new ResourceCommandRejectedException($"Command '{command.Id}' cannot delete the newer declaration '{existing.Command.Id}' for key '{command.Key}'.");
                }
                foreach (var entry in _commands.Values)
                {
                    if (entry.Command.Id == command.Id && entry.Command.Owner != command.Owner)
                    {
                        throw new ResourceCommandRejectedException($"Command id '{command.Id}' belongs to owner '{entry.Command.Owner}'; owner '{command.Owner}' must use a different identity.");
                    }
                    if (entry.Command.Owner == command.Owner && entry.Command.Id == command.Id &&
                        (entry.Command.Kind != command.Kind || entry.Command.Key != command.Key ||
                         (!delete && !entry.Command.Payload.Span.SequenceEqual(command.Payload.Span))))
                    {
                        throw new ResourceCommandRejectedException($"Command id '{command.Id}' for owner '{command.Owner}' was already used for a different declaration.");
                    }
                }
                if (!delete && found && existing.Command.Id == command.Id)
                {
                    return existing.Response.ToArray();
                }
            }

            if (delete && !found)
            {
                return ReadOnlyMemory<byte>.Empty;
            }
            ReadOnlyMemory<byte> response = delete
                ? await handler.DeleteAsync(existing.Command, cancellationToken).ConfigureAwait(false)
                : await handler.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (delete)
                {
                    _commands.Remove(command.Key);
                }
                else
                {
                    _commands[command.Key] = (command, response.ToArray());
                }
            }
            return response;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async ValueTask<ResourceHealthReport> CheckContributorsAsync(CancellationToken cancellationToken)
    {
        IHealthContributor[] contributors;
        lock (_sync)
        {
            contributors = _contributors.Values.ToArray();
        }

        var results = new Dictionary<string, HealthContribution>(contributors.Length, StringComparer.Ordinal);
        HealthStatus status = HealthStatus.Healthy;

        foreach (IHealthContributor contributor in contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HealthContribution contribution;
            try
            {
                contribution = await contributor.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                contribution = HealthContribution.Unhealthy(exception.Message);
            }

            results.Add(contributor.Name, contribution);
            if ((int)contribution.Status < (int)status)
            {
                status = contribution.Status;
            }
        }

        return new ResourceHealthReport(
            status,
            new ReadOnlyDictionary<string, HealthContribution>(results));
    }
}
