using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

internal interface IInProcessProbeRunner
{
    Task<InProcessProbeResult> RunAsync(
        InProcessProbeConfiguration probe,
        ResourceContext context,
        CancellationToken cancellationToken);
}

internal sealed class InProcessProbeRunner : IInProcessProbeRunner
{
    private readonly InProcessGatewayOptions _options;

    internal InProcessProbeRunner(InProcessGatewayOptions options)
    {
        _options = options;
    }

    public async Task<InProcessProbeResult> RunAsync(
        InProcessProbeConfiguration configuration,
        ResourceContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ProbeMapping probe = configuration.Mapping;
        using var timeout = new CancellationTokenSource(
            _options.ProbeTimeout,
            _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            return probe.Kind switch
            {
                ProbeKind.Http => await RunHttpAsync(
                    probe,
                    context,
                    configuration.IsDefaultControlPlane,
                    linked.Token).ConfigureAwait(false),
                ProbeKind.Tcp => await RunTcpAsync(probe, context, linked.Token).ConfigureAwait(false),
                ProbeKind.None => InProcessProbeResult.Success("Probe is disabled."),
                ProbeKind.Exec => InProcessProbeResult.Failure(
                    "In-process execution cannot isolate an exec probe."),
                ProbeKind.Grpc => InProcessProbeResult.Failure(
                    "In-process execution does not support gRPC probes."),
                _ => throw new InvalidDataException(
                    $"Unsupported in-process probe kind '{probe.Kind}'."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return InProcessProbeResult.Failure(
                $"{probe.Role} probe timed out after {_options.ProbeTimeout}.");
        }
        catch (Exception exception) when (exception is HttpRequestException
            or SocketException
            or InvalidOperationException
            or InvalidDataException)
        {
            return InProcessProbeResult.Failure(
                $"{probe.Role} probe failed: {exception.Message}");
        }
    }

    private static async Task<InProcessProbeResult> RunHttpAsync(
        ProbeMapping probe,
        ResourceContext context,
        bool authenticate,
        CancellationToken cancellationToken)
    {
        Uri address = ResolveAddress(probe, context, includePath: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (authenticate && !context.BootstrapCredential.IsEmpty)
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer " + Encoding.UTF8.GetString(context.BootstrapCredential.Span));
        }

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        handler.SslOptions.RemoteCertificateValidationCallback = context.CreateOutboundTrustValidator();
        using var client = new HttpClient(handler);
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return InProcessProbeResult.Success(
                $"HTTP {probe.Role} probe '{address}' returned 200.");
        }

        bool failFast = response.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.MethodNotAllowed;
        return InProcessProbeResult.Failure(
            $"HTTP {probe.Role} probe '{address}' returned {(int)response.StatusCode} "
            + $"({response.ReasonPhrase})."
            + (failFast ? " Verify the resource control-plane endpoint and path." : string.Empty),
            failFast);
    }

    private static async Task<InProcessProbeResult> RunTcpAsync(
        ProbeMapping probe,
        ResourceContext context,
        CancellationToken cancellationToken)
    {
        Uri address = ResolveAddress(probe, context, includePath: false);
        using var client = new TcpClient();
        await client.ConnectAsync(address.IdnHost, address.Port, cancellationToken)
            .ConfigureAwait(false);
        return InProcessProbeResult.Success(
            $"TCP {probe.Role} probe '{address.Host}:{address.Port}' connected.");
    }

    private static Uri ResolveAddress(
        ProbeMapping probe,
        ResourceContext context,
        bool includePath)
    {
        if (string.IsNullOrWhiteSpace(probe.Endpoint))
        {
            throw new InvalidDataException(
                $"The {probe.Role} network probe does not name an endpoint.");
        }

        if (!context.Endpoints.TryGetValue(probe.Endpoint, out Uri? endpoint))
        {
            throw new InvalidDataException(
                $"The {probe.Role} probe endpoint '{probe.Endpoint}' is not bound.");
        }
        if (!includePath)
        {
            return endpoint;
        }

        var builder = new UriBuilder(endpoint)
        {
            Path = probe.Value ?? "/",
        };
        return builder.Uri;
    }
}

internal readonly record struct InProcessProbeResult(
    bool Succeeded,
    bool FailFast,
    string Detail)
{
    internal static InProcessProbeResult Success(string detail) => new(true, false, detail);

    internal static InProcessProbeResult Failure(string detail, bool failFast = false) =>
        new(false, failFast, detail);
}
