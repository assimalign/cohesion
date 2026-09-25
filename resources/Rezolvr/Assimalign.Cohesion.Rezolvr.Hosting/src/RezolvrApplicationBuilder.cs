using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting.Internal;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

/// <summary>
/// Composes a Rezolvr application and its hosting services.
/// </summary>
public sealed class RezolvrApplicationBuilder : IRezolvrApplicationBuilder
{
    private readonly List<Func<RezolvrApplicationContext, IHostService>> _serviceFactories = [];

    private readonly ILoggerFactory? _loggerFactory;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal RezolvrApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered Rezolvr control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out IHostService? telemetry);
            if (telemetry is not null)
            {
                _serviceFactories.Insert(0, _ => telemetry);
            }
        }
    }

    /// <summary>
    /// Adds an existing host service to the resolver application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public RezolvrApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the resolver host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    public RezolvrApplicationBuilder AddService(Func<RezolvrApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    /// <summary>
    /// Builds the Rezolvr application.
    /// </summary>
    /// <returns>The configured Rezolvr application.</returns>
    public RezolvrApplication Build()
    {
        var options = new RezolvrApplicationOptions();
        var context = new RezolvrApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("admin");
        var hostedServices = new IHostService[_serviceFactories.Count + (hasEndpoint ? 1 : 0)];

        for (var index = 0; index < _serviceFactories.Count; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A resolver service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            string fallback = Path.GetFullPath(Path.Combine(_resourceContext!.ContentRootPath, "data"));
            ResourceMount mount = _resourceContext.GetMount("data", fallback);
            if (string.IsNullOrWhiteSpace(mount.Path))
            {
                throw new InvalidOperationException("The Rezolvr data directory must expose a file-system path.");
            }
            var repository = new RezolvrRecordRepository(mount.Path);
            foreach (string kind in _controlPlane.AcceptedCommandKinds)
            {
                if (kind is RezolvrResourceCommandHandler.AddARecord or RezolvrResourceCommandHandler.AddCnameRecord)
                {
                    _controlPlane.RegisterCommandHandler(new RezolvrResourceCommandHandler(kind, repository));
                }
            }
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["admin"];
                _controlPlane.ObserveEndpoint("admin", endpoint);
                hostedServices[^1] = new RezolvrControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context, repository);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new RezolvrApplication(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IRezolvrApplication IRezolvrApplicationBuilder.Build() => Build();
}
