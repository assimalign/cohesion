using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed class LocalProbeRunner
{
    private readonly LocalGatewayOptions _options;

    public LocalProbeRunner(LocalGatewayOptions options)
    {
        _options = options;
    }

    public async Task<ProbeAttemptResult> RunAsync(
        IProbeSpec probe,
        LocalResourceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.ProbeTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return probe.Kind switch
            {
                ProbeKind.Http => await RunHttpAsync(probe, configuration, linked.Token).ConfigureAwait(false),
                ProbeKind.Tcp => await RunTcpAsync(probe, configuration, linked.Token).ConfigureAwait(false),
                ProbeKind.Exec => await RunExecAsync(probe, configuration, linked.Token).ConfigureAwait(false),
                ProbeKind.None => ProbeAttemptResult.Success("Probe is disabled."),
                ProbeKind.Grpc => throw new NotSupportedException(
                    "The local gateway does not yet support gRPC health probes."),
                _ => throw new InvalidDataException($"Unsupported local probe kind '{probe.Kind}'.")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeAttemptResult.Failure(
                $"{Describe(probe, configuration)} timed out after {_options.ProbeTimeout}.");
        }
        catch (HttpRequestException exception)
        {
            return ProbeAttemptResult.Failure(
                $"{Describe(probe, configuration)} failed: {exception.Message}");
        }
        catch (SocketException exception)
        {
            return ProbeAttemptResult.Failure(
                $"{Describe(probe, configuration)} failed: {exception.Message}");
        }
        catch (Win32Exception exception)
        {
            return ProbeAttemptResult.Failure(
                $"{Describe(probe, configuration)} failed to start: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return ProbeAttemptResult.Failure(
                $"{Describe(probe, configuration)} failed: {exception.Message}");
        }
    }

    private async Task<ProbeAttemptResult> RunHttpAsync(
        IProbeSpec probe,
        LocalResourceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Uri address = ResolveAddress(probe, configuration, includePath: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (probe is LocalControlPlaneProbe controlPlaneProbe &&
            !controlPlaneProbe.BootstrapCredential.IsEmpty)
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer " + Encoding.UTF8.GetString(controlPlaneProbe.BootstrapCredential.Span));
        }

        configuration.Environment.TryGetValue(ResourceEnvironment.TrustBundlePath, out string? trustPath);
        ResourceContext context = ResourceContext.FromEnvironment(new Dictionary<string, string?> { [ResourceEnvironment.TrustBundlePath] = trustPath });
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        handler.SslOptions.RemoteCertificateValidationCallback = context.CreateOutboundTrustValidator();
        using var client = new HttpClient(handler);
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return ProbeAttemptResult.Success($"HTTP probe '{address.ToEndpointString()}' returned 200.");
        }

        bool failFast = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
        return ProbeAttemptResult.Failure(
            $"HTTP probe '{address.ToEndpointString()}' returned {(int)response.StatusCode} ({response.ReasonPhrase})."
                + (failFast ? " Verify the resource control-plane endpoint and path." : string.Empty),
            failFast);
    }

    private static async Task<ProbeAttemptResult> RunTcpAsync(
        IProbeSpec probe,
        LocalResourceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Uri address = ResolveAddress(probe, configuration, includePath: false);
        using var client = new TcpClient();
        await client.ConnectAsync(address.IdnHost, address.Port, cancellationToken).ConfigureAwait(false);
        return ProbeAttemptResult.Success(
            $"TCP probe '{address.Host}:{address.Port}' connected.");
    }

    private static async Task<ProbeAttemptResult> RunExecAsync(
        IProbeSpec probe,
        LocalResourceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (probe.Command.Count == 0)
        {
            throw new InvalidDataException("An exec probe must contain an executable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = probe.Command[0],
            WorkingDirectory = Path.GetDirectoryName(configuration.Artifact.ExecutablePath)
                ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        for (int index = 1; index < probe.Command.Count; index++)
        {
            startInfo.ArgumentList.Add(probe.Command[index]);
        }

        foreach (KeyValuePair<string, string> variable in configuration.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return ProbeAttemptResult.Failure($"Exec probe '{probe.Command[0]}' did not start.");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

            return process.ExitCode == 0
                ? ProbeAttemptResult.Success($"Exec probe '{probe.Command[0]}' exited with code 0.")
                : ProbeAttemptResult.Failure(
                    $"Exec probe '{probe.Command[0]}' exited with code {process.ExitCode}: {stderr.Result.Trim()}");
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The probe exited between HasExited and Kill.
            }

            throw;
        }
    }

    private static Uri ResolveAddress(
        IProbeSpec probe,
        LocalResourceConfiguration configuration,
        bool includePath)
    {
        if (probe.Address is Uri absolute)
        {
            return Uri.ThrowIfNotEndpoint(absolute);
        }

        if (probe.Endpoint is null)
        {
            throw new InvalidDataException("A network probe must declare an endpoint or absolute address.");
        }

        foreach (ResourceEndpoint endpoint in configuration.ObservedEndpoints)
        {
            if (string.Equals(endpoint.Name, probe.Endpoint, StringComparison.Ordinal))
            {
                return Uri.CreateEndpoint(
                    endpoint.Scheme,
                    endpoint.Host ?? "127.0.0.1",
                    endpoint.Port,
                    includePath ? probe.Path : null);
            }
        }

        throw new InvalidDataException(
            $"Probe endpoint '{probe.Endpoint}' was not allocated for resource '{configuration.Resource.Name}'.");
    }

    private static string Describe(IProbeSpec probe, LocalResourceConfiguration configuration)
    {
        try
        {
            return probe.Kind switch
            {
                ProbeKind.Http => $"HTTP probe '{ResolveAddress(probe, configuration, includePath: true).ToEndpointString()}'",
                ProbeKind.Tcp => $"TCP probe '{ToAuthority(ResolveAddress(probe, configuration, includePath: false))}'",
                ProbeKind.Exec when probe.Command.Count > 0 => $"Exec probe '{probe.Command[0]}'",
                _ => $"{probe.Kind} probe"
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return $"{probe.Kind} probe";
        }
    }

    private static string ToAuthority(Uri address) => $"{address.Host}:{address.Port}";
}
