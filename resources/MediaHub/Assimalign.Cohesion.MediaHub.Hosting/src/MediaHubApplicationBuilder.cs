using System;
using System.Collections.Generic;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.MediaHub;

namespace Assimalign.Cohesion.MediaHub.Hosting;

/// <summary>
/// Composes a MediaHub application and its hosting services.
/// </summary>
public sealed class MediaHubApplicationBuilder : IMediaHubApplicationBuilder
{
    private readonly List<Func<MediaHubApplicationContext, IHostService>> _serviceFactories = [];

    private readonly ILoggerFactory? _loggerFactory;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal MediaHubApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered MediaHub control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out IHostService? telemetry);
            if (telemetry is not null)
            {
                _serviceFactories.Insert(0, _ => telemetry);
            }
        }
    }

    /// <summary>
    /// Adds an existing host service to the media hub application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public MediaHubApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the media hub host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    public MediaHubApplicationBuilder AddService(Func<MediaHubApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    /// <summary>
    /// Builds the media hub application.
    /// </summary>
    /// <returns>The configured media hub application.</returns>
    public MediaHubApplication Build()
    {
        var options = new MediaHubApplicationOptions();
        var context = new MediaHubApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("http");
        var hostedServices = new IHostService[_serviceFactories.Count + (hasEndpoint ? 1 : 0)];

        for (var index = 0; index < _serviceFactories.Count; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A media hub service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["http"];
                _controlPlane.ObserveEndpoint("http", endpoint);
                hostedServices[^1] = new MediaHubControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new MediaHubApplication(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IMediaHubApplication IMediaHubApplicationBuilder.Build() => Build();
}
