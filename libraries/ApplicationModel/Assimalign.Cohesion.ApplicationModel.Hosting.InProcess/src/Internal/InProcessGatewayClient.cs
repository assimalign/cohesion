using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.Internal;

/// <summary>
/// The in-process platform's gateway client: it publishes to the gateway registered at the endpoint
/// the instance's environment names, and attaches the instance's control plane to that gateway, so
/// the gateway calls it directly.
/// </summary>
/// <remarks>
/// <para>
/// Nothing crosses a process boundary, so nothing is serialized on the way out: the gateway receives
/// the manifest itself. Its calls in carry their content as bytes, which the control plane reads with
/// its own serializer, as it would read a request.
/// </para>
/// <para>
/// The gateway reaches the control plane through the endpoint the instance publishes to, so the
/// manifest the client publishes names that endpoint as the control plane's.
/// </para>
/// </remarks>
internal sealed class InProcessGatewayClient : IApplicationResourceInstanceGatewayClient
{
    private readonly string? _endpoint;
    private readonly Lock _lock = new();
    private IDisposable? _attachment;

    /// <param name="endpoint">The gateway's endpoint, as the instance's environment names it; read when the resource opts in.</param>
    public InProcessGatewayClient(string? endpoint)
    {
        _endpoint = endpoint;
    }

    public PlatformName Platform => InProcessPlatform.Name;

    /// <exception cref="InvalidOperationException">
    /// The environment names no in-process endpoint, or no gateway receives at it.
    /// </exception>
    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        (Uri endpoint, IInProcessGateway gateway) = Resolve();
        return gateway.PublishAsync(manifest with { ControlPlaneEndpoint = endpoint }, cancellationToken);
    }

    /// <exception cref="InvalidOperationException">
    /// The environment names no in-process endpoint, or no gateway receives at it.
    /// </exception>
    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        (Uri endpoint, IInProcessGateway gateway) = Resolve();
        return gateway.RenewAsync(manifest with { ControlPlaneEndpoint = endpoint }, cancellationToken);
    }

    /// <exception cref="InvalidOperationException">
    /// The client already carries calls to a control plane, the environment names no in-process
    /// endpoint, or no gateway receives at it.
    /// </exception>
    public ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        lock (_lock)
        {
            if (_attachment is not null)
            {
                throw new InvalidOperationException(ErrorMessages.ClientAlreadyStarted);
            }

            // Captured here, in the instance's flow, so the gateway's calls run as the instance's own
            // requests would: in its environment, not the gateway's.
            (_, IInProcessGateway gateway) = Resolve();
            _attachment = gateway.Attach(new InProcessControlPlane(controlPlane, ExecutionContext.Capture()));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        IDisposable? attachment;
        lock (_lock)
        {
            attachment = _attachment;
            _attachment = null;
        }

        attachment?.Dispose();
        return ValueTask.CompletedTask;
    }

    // The gateway registered at the endpoint when the call is made, as an HTTP client reaches whatever
    // listens at its URL.
    private (Uri Endpoint, IInProcessGateway Gateway) Resolve()
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            throw new InvalidOperationException(Format(ErrorMessages.EndpointMissing, AppEnvironment.Variables.ManifestEndpoint));
        }

        if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out Uri? endpoint) || !InProcessPlatform.IsEndpoint(endpoint))
        {
            throw new InvalidOperationException(Format(ErrorMessages.EndpointVariableInvalid, AppEnvironment.Variables.ManifestEndpoint, _endpoint));
        }

        IInProcessGateway gateway = InProcessPlatform.FindGateway(endpoint)
            ?? throw new InvalidOperationException(Format(ErrorMessages.GatewayUnknown, endpoint.AbsoluteUri));
        return (endpoint, gateway);
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}
