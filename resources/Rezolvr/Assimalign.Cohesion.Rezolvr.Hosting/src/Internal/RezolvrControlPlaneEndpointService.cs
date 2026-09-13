using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ControlPlane;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

internal sealed class RezolvrControlPlaneEndpointService : IHostService, IDisposable
{
    private readonly WebApplication _application;
    private readonly IResourceControlPlane _controlPlane;
    private readonly RezolvrRecordRepository _repository;


    internal RezolvrControlPlaneEndpointService(
        Uri endpoint,
        IResourceControlPlane controlPlane,
        ResourceContext resourceContext,
        RezolvrApplicationContext applicationContext,
        RezolvrRecordRepository repository)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(applicationContext);
        _controlPlane = controlPlane;
        _repository = repository;
        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Rezolvr control-plane endpoint 'admin' requires http.");
        }

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        // Parameterless construction deliberately avoids resource registration on this private host.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));
        _application = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _application;
        pipeline.UseResourceControlPlane(controlPlane, resourceContext,
            () => applicationContext.State is HostState.Started);
    }

    public ServiceId Id { get; } = ServiceId.New();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (ResourceCommand command in await _repository.ReadCommandsAsync(cancellationToken).ConfigureAwait(false))
        {
            await _controlPlane.ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        await ((IHost)_application).StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        ((IHost)_application).StopAsync(cancellationToken);

    public void Dispose()
    {
        ((IDisposable)_application).Dispose();

    }

    private static IPAddress ResolveBindAddress(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.TryParse(host, out IPAddress? address)
                ? address
                : throw new InvalidOperationException($"The Rezolvr endpoint host '{host}' is not a bindable IP address.");

}
