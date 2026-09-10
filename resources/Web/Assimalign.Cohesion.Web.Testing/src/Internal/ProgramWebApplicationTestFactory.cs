using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Testing.Internal;

internal sealed class ProgramWebApplicationTestFactory : IWebApplicationProgramTestFactory
{
    private const string HttpEndpointName = "http";
    private const string ReadinessPath = "/readyz";
    private const string StopPath = "/cohesion/v1/stop";

    private readonly Assembly _assembly;
    private readonly string[] _arguments;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly TimeSpan _probeInterval;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _startupCancellation = new();

    private IResourceEntryInvocation? _invocation;
    private IHost? _host;
    private IWebApplication? _application;
    private Task? _start;
    private bool _isStarted;
    private bool _isDisposed;

    internal ProgramWebApplicationTestFactory(
        Type programType,
        WebApplicationProgramTestFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(programType);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Arguments);

        if (options.StartupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.StartupTimeout,
                "The startup timeout must be greater than zero.");
        }
        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ShutdownTimeout,
                "The shutdown timeout must be greater than zero.");
        }
        if (options.ProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ProbeInterval,
                "The probe interval must be greater than zero.");
        }

        _assembly = programType.Assembly;
        MethodInfo entryPoint = _assembly.EntryPoint
            ?? throw new InvalidOperationException(
                $"Assembly '{_assembly.GetName().Name}' has no executable entry point.");
        if (!ReferenceEquals(entryPoint.DeclaringType, programType))
        {
            throw new InvalidOperationException(
                $"Type '{programType.FullName}' is not the entry-point Program for assembly " +
                $"'{_assembly.GetName().Name}'.");
        }

        _arguments = (string[])options.Arguments.Clone();
        _startupTimeout = options.StartupTimeout;
        _shutdownTimeout = options.ShutdownTimeout;
        _probeInterval = options.ProbeInterval;
        ResourceContext = options.ResourceContext ?? CreateDefaultContext(programType);

        if (!ResourceContext.Endpoints.TryGetValue(HttpEndpointName, out Uri? endpoint))
        {
            throw new ArgumentException(
                $"The test resource context must provide the Web '{HttpEndpointName}' endpoint.",
                nameof(options));
        }
        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The test resource context's '{HttpEndpointName}' endpoint must use http.",
                nameof(options));
        }
        if (ResourceContext.GatewayName is not null && ResourceContext.BootstrapCredential.IsEmpty)
        {
            throw new ArgumentException(
                "A managed test resource context must provide a bootstrap credential so the factory can request graceful stop.",
                nameof(options));
        }
    }

    public ResourceContext ResourceContext { get; }

    public IWebApplication Application
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                return _application ?? throw new InvalidOperationException(
                    "The resource Program has not started. Call StartAsync or CreateClient first.");
            }
        }
    }

    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _isStarted;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task start;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _start ??= StartCoreAsync(_startupCancellation.Token);
            start = _start;
        }

        using var timeout = new CancellationTokenSource(_startupTimeout);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await start.WaitAsync(startup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _startupCancellation.Cancel();
            await TryAbortStartAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _startupCancellation.Cancel();
            await TryAbortStartAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"The Web resource did not report ready within {_startupTimeout}.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        IResourceEntryInvocation? invocation;
        lock (_gate)
        {
            invocation = _invocation;
        }

        if (invocation is null)
        {
            return;
        }

        if (!invocation.Completion.IsCompleted)
        {
            await RequestStopAsync(invocation.Completion, cancellationToken).ConfigureAwait(false);
        }

        await invocation.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _isStarted = false;
        }
    }

    public HttpClient CreateClient()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }

        if (!IsStarted)
        {
            StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return CreateHttpClient();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        _startupCancellation.Cancel();
        using var shutdown = new CancellationTokenSource(_shutdownTimeout);
        try
        {
            await StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            IHost? host;
            lock (_gate)
            {
                host = _host;
            }

            if (host is not null)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "FromProgram<TProgram> roots all public and non-public methods on the validated entry-point type.")]
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        IResourceEntryInvocation invocation;
        using (ResourceRuntime.CreateScope(ResourceContext))
        {
            invocation = ResourceRuntime.InvokeEntry(_assembly, _arguments);
        }

        lock (_gate)
        {
            _invocation = invocation;
        }

        IHost host = await invocation.HostReady.WaitAsync(cancellationToken).ConfigureAwait(false);
        IWebApplication application = host as IWebApplication
            ?? throw new InvalidOperationException(
                $"Resource entry point '{_assembly.GetName().Name}' built a non-Web host.");

        lock (_gate)
        {
            _host = host;
            _application = application;
        }

        await WaitForReadinessAsync(invocation.Completion, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _isStarted = true;
        }
    }

    private async Task WaitForReadinessAsync(
        Task program,
        CancellationToken cancellationToken)
    {
        using HttpClient client = CreateHttpClient();

        while (true)
        {
            if (program.IsCompleted)
            {
                await program.ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The resource program exited before its Web control plane reported ready.");
            }

            try
            {
                using HttpResponseMessage response = await client
                    .GetAsync(ReadinessPath, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listener has not bound yet; retry within StartAsync's outer budget.
            }

            await Task.Delay(_probeInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryAbortStartAsync()
    {
        using var shutdown = new CancellationTokenSource(_shutdownTimeout);
        try
        {
            await StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            ScheduleStopAfterDelayedBuild();
        }
        catch (Exception)
        {
            // Startup cancellation and timeout are the primary failures. A Program fault that
            // races its best-effort shutdown must not replace the caller's original exception.
        }
    }

    private void ScheduleStopAfterDelayedBuild()
    {
        IResourceEntryInvocation? invocation;
        lock (_gate)
        {
            invocation = _invocation;
        }

        if (invocation is not null)
        {
            _ = StopAfterDelayedBuildAsync(invocation);
        }
    }

    private async Task StopAfterDelayedBuildAsync(IResourceEntryInvocation invocation)
    {
        try
        {
            Task completed = await Task
                .WhenAny(invocation.HostReady, invocation.Completion)
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, invocation.Completion))
            {
                await invocation.Completion.ConfigureAwait(false);
                return;
            }

            using var shutdown = new CancellationTokenSource(_shutdownTimeout);
            await RequestStopAsync(invocation.Completion, shutdown.Token).ConfigureAwait(false);
            await invocation.Completion.WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Detached cleanup cannot surface through the already-completed StartAsync call.
        }
    }

    private async Task RequestStopAsync(Task program, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateHttpClient();

        while (!program.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, StopPath);
                AuthorizeControlPlaneRequest(request);
                using HttpResponseMessage response = await client
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                throw new HttpRequestException(
                    $"The Web control plane rejected graceful stop with HTTP {(int)response.StatusCode}.");
            }
            catch (HttpRequestException)
            {
                if (program.IsCompleted)
                {
                    return;
                }

                await Task.Delay(_probeInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ResourceContext CreateDefaultContext(Type programType)
    {
        int httpPort = ReservePort();
        string bootstrapCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var endpoints = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
        {
            [HttpEndpointName] = Uri.CreateEndpoint("http", "127.0.0.1", httpPort),
        };

        return new ResourceContext(
            applicationName: "tests",
            resourceName: programType.Assembly.GetName().Name ?? programType.Name,
            environmentName: "Testing",
            gatewayName: "inprocess",
            contentRootPath: AppContext.BaseDirectory,
            endpoints: endpoints,
            bootstrapCredential: Encoding.UTF8.GetBytes(bootstrapCredential));
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            BaseAddress = ResourceContext.Endpoints[HttpEndpointName],
        };
    }

    private void AuthorizeControlPlaneRequest(HttpRequestMessage request)
    {
        if (!ResourceContext.BootstrapCredential.IsEmpty)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                Encoding.UTF8.GetString(ResourceContext.BootstrapCredential.Span));
        }
    }
}
