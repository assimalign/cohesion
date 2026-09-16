using System;
using System.Collections.Generic;
using System.Reflection;

using Assimalign.Cohesion.EventHub;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.EventHub.Hosting;

/// <summary>
/// Composes an EventHub application and its hosting services.
/// </summary>
public sealed class EventHubApplicationBuilder : IEventHubApplicationBuilder
{
    private readonly List<Func<EventHubApplicationContext, IHostService>> _serviceRegistrations = new();

    private readonly ILoggerFactory? _loggerFactory;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

    internal EventHubApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered EventHub control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out IHostService? telemetry);
            if (telemetry is not null)
            {
                _serviceRegistrations.Insert(0, _ => telemetry);
            }
        }
    }

    /// <summary>
    /// Registers a host service with the event hub application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public EventHubApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the event hub application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once for each call to <see cref="Build"/> and receives that
    /// application's final host context. The resulting service follows registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory returns <see langword="null"/> when the application is built.</exception>
    public EventHubApplicationBuilder AddService(Func<EventHubApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    /// <summary>
    /// Builds the event hub application.
    /// </summary>
    /// <returns>The configured event hub application.</returns>
    /// <exception cref="InvalidOperationException">A registered host service factory returns <see langword="null"/>.</exception>
    public EventHubApplication Build()
    {
        var options = new EventHubApplicationOptions();
        var context = new EventHubApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("http");
        var hostedServices = new IHostService[_serviceRegistrations.Count + (hasEndpoint ? 1 : 0)];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The event hub application service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["http"];
                _controlPlane.ObserveEndpoint("http", endpoint);
                hostedServices[^1] = new EventHubControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new EventHubApplication(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    IEventHubApplication IEventHubApplicationBuilder.Build() => Build();
}
