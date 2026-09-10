using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane;

internal sealed class GatewayControlPlaneFactory : IApplicationGatewayControlPlaneFactory
{
    private readonly string? _metadataDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<IResourceCommandDispatcher> _dispatchers;

    public GatewayControlPlaneFactory(GatewayControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MetadataDirectory is not null && string.IsNullOrWhiteSpace(options.MetadataDirectory))
        {
            throw new ArgumentException(
                "MetadataDirectory must not be empty when specified.",
                nameof(options));
        }

        _metadataDirectory = options.MetadataDirectory is null
            ? null
            : Path.GetFullPath(options.MetadataDirectory);
        _timeProvider = options.TimeProvider ?? throw new ArgumentException(
            "TimeProvider must not be null.",
            nameof(options));

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var dispatchers = new IResourceCommandDispatcher[options.CommandDispatchers.Count];
        for (int index = 0; index < dispatchers.Length; index++)
        {
            IResourceCommandDispatcher dispatcher = options.CommandDispatchers[index]
                ?? throw new ArgumentException(
                    $"Command dispatcher at index {index} is null.",
                    nameof(options));
            ArgumentException.ThrowIfNullOrWhiteSpace(dispatcher.ResourceKind);
            if (!kinds.Add(dispatcher.ResourceKind))
            {
                throw new ArgumentException(
                    $"More than one command dispatcher handles resource kind '{dispatcher.ResourceKind}'.",
                    nameof(options));
            }

            dispatchers[index] = dispatcher;
        }

        _dispatchers = dispatchers;
    }

    public IApplicationGatewayControlPlane Create(ApplicationName application) =>
        new GatewayControlPlaneServer(
            application,
            _metadataDirectory,
            _timeProvider,
            _dispatchers);
}
