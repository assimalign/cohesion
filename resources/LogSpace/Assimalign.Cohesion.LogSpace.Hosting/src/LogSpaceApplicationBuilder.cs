using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.LogSpace.Hosting.Internal;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.LogSpace;

namespace Assimalign.Cohesion.LogSpace.Hosting;

/// <summary>
/// Composes a LogSpace application and its hosting services.
/// </summary>
public sealed class LogSpaceApplicationBuilder : ILogSpaceApplicationBuilder
{
    private readonly List<Func<LogSpaceApplicationContext, IHostService>> _serviceRegistrations = new();

    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IHostService? _telemetry;

    internal LogSpaceApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException("The registered LogSpace control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out _telemetry);
        }
    }

    /// <summary>
    /// Registers a host service with the LogSpace application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public LogSpaceApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the LogSpace application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once for each call to <see cref="Build"/> and receives that
    /// application's final host context. The resulting service follows registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory returns <see langword="null"/> when the application is built.</exception>
    public LogSpaceApplicationBuilder AddService(Func<LogSpaceApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    /// <summary>
    /// Builds the LogSpace application.
    /// </summary>
    /// <returns>The configured LogSpace application.</returns>
    /// <exception cref="InvalidOperationException">A registered host service factory returns <see langword="null"/>.</exception>
    public LogSpaceApplication Build()
    {
        var options = new LogSpaceApplicationOptions();
        var context = new LogSpaceApplicationContext(_resourceContext);
        bool hasEndpoint = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("query");
        bool hasOtlp = _controlPlane is not null && _resourceContext!.Endpoints.ContainsKey("otlp");
        var hostedServices = new IHostService[_serviceRegistrations.Count + (hasEndpoint ? 1 : 0) + (hasOtlp ? 2 : 0) + (_telemetry is null ? 0 : 1)];
        int serviceIndex = 0;
        if (_telemetry is not null) { hostedServices[serviceIndex++] = _telemetry; }
        LogSegmentStore? store = null;
        if (hasOtlp)
        {
            store = new LogSegmentStore(_resourceContext!.GetMount("data", Path.Combine(_resourceContext.ContentRootPath, "logs")).Path
                ?? throw new InvalidOperationException("LogSpace data requires a filesystem mount."));
            hostedServices[serviceIndex++] = new SegmentFlushService(store);
        }

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[serviceIndex++] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The LogSpace application service factory returned null.");
        }

        if (_controlPlane is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (hasEndpoint)
            {
                Uri endpoint = _resourceContext!.Endpoints["query"];
                _controlPlane.ObserveEndpoint("query", endpoint);
                hostedServices[serviceIndex++] = new LogSpaceControlPlaneEndpointService(endpoint, _controlPlane, _resourceContext, context, store);
            }
            if (hasOtlp)
            {
                Uri endpoint = _resourceContext!.Endpoints["otlp"];
                _controlPlane.ObserveEndpoint("otlp", endpoint);
                hostedServices[serviceIndex++] = new OtlpReceiverEndpointService(endpoint, store!, _resourceContext, context);
            }
        }
        context.SetHostedServices(hostedServices);

        var application = new LogSpaceApplication(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }
        return application;
    }

    ILogSpaceApplication ILogSpaceApplicationBuilder.Build() => Build();
}
