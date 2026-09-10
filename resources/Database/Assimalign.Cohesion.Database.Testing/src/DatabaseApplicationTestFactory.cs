using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Testing;

/// <summary>
/// Invokes a Cohesion database resource's real <c>Program.Main</c> under a test-scoped
/// <see cref="ResourceContext"/> and manages it through the generated Database control plane.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FromProgram{TProgram}()"/> uses <c>TProgram</c> only as the statically rooted
/// entry-assembly marker. It invokes <see cref="Assembly.EntryPoint"/>, the one reflection
/// operation sanctioned for compiler-synthesized top-level entry points. The generic
/// preservation annotation retains public and compiler-generated non-public methods for
/// trimming and NativeAOT; the factory performs no assembly scanning, dynamic loading, or
/// runtime code generation.
/// </para>
/// <para>
/// A top-level resource program must expose its compiler-generated marker to its test project
/// with <c>public partial class Program { }</c>. Its normal top-level statements remain the
/// only composition path; the factory does not require a test-only bootstrap or interface.
/// </para>
/// </remarks>
public sealed class DatabaseApplicationTestFactory : IDatabaseApplicationTestFactory
{
    private const string AdminEndpointName = "admin";
    private const string DatabaseEndpointName = "db";
    private const string DataMountName = "data";
    private const string ReadinessPath = "/readyz";
    private const string StopPath = "/cohesion/v1/stop";

    private readonly Assembly _assembly;
    private readonly string[] _arguments;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly TimeSpan _probeInterval;
    private readonly string? _ownedDataPath;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _startupCancellation = new();

    private IResourceEntryInvocation? _invocation;
    private IHost? _host;
    private Task? _start;
    private bool _isStarted;
    private bool _isDisposed;

    private DatabaseApplicationTestFactory(
        MethodInfo entryPoint,
        DatabaseApplicationTestFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
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

        _assembly = entryPoint.DeclaringType!.Assembly;
        _arguments = (string[])options.Arguments.Clone();
        _startupTimeout = options.StartupTimeout;
        _shutdownTimeout = options.ShutdownTimeout;
        _probeInterval = options.ProbeInterval;

        if (options.ResourceContext is null)
        {
            (ResourceContext, _ownedDataPath) = CreateDefaultContext(entryPoint.DeclaringType!);
        }
        else
        {
            ResourceContext = options.ResourceContext;
        }

        if (!ResourceContext.Endpoints.ContainsKey(AdminEndpointName))
        {
            throw new ArgumentException(
                $"The test resource context must provide the Database '{AdminEndpointName}' endpoint.",
                nameof(options));
        }
    }

    /// <inheritdoc />
    public ResourceContext ResourceContext { get; }

    /// <inheritdoc />
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

    /// <summary>
    /// Creates a factory for the resource program declared by <typeparamref name="TProgram"/>,
    /// using an isolated default test context.
    /// </summary>
    /// <typeparam name="TProgram">
    /// The resource executable's top-level <c>Program</c> marker. Declare it public and partial
    /// in the resource project so the test project can name it.
    /// </typeparam>
    /// <returns>An unstarted factory for the resource program.</returns>
    /// <exception cref="InvalidOperationException">
    /// The marker's assembly has no supported static entry point, or its entry point is not
    /// declared by <typeparamref name="TProgram"/>.
    /// </exception>
    public static DatabaseApplicationTestFactory FromProgram<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)] TProgram>()
    {
        return FromProgram<TProgram>(new DatabaseApplicationTestFactoryOptions());
    }

    /// <summary>
    /// Creates a factory for the resource program declared by <typeparamref name="TProgram"/>
    /// with explicit invocation options.
    /// </summary>
    /// <typeparam name="TProgram">
    /// The resource executable's top-level <c>Program</c> marker. Declare it public and partial
    /// in the resource project so the test project can name it.
    /// </typeparam>
    /// <param name="options">The context, arguments, and lifecycle budgets for the invocation.</param>
    /// <returns>An unstarted factory for the resource program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The marker's assembly has no supported static entry point, or its entry point is not
    /// declared by <typeparamref name="TProgram"/>.
    /// </exception>
    public static DatabaseApplicationTestFactory FromProgram<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)] TProgram>(
        DatabaseApplicationTestFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        MethodInfo entryPoint = ResolveEntryPoint<TProgram>();
        return new DatabaseApplicationTestFactory(entryPoint, options);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task start;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_isStarted)
            {
                return;
            }

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
                $"The Database resource did not report ready within {_startupTimeout}.");
        }
    }

    /// <inheritdoc />
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

    /// <summary>
    /// Gracefully stops the resource program when it was started, then removes the temporary
    /// data mount owned by a default-context factory. Disposal is idempotent.
    /// </summary>
    /// <returns>A task that completes when the factory has released its resources.</returns>
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

            DeleteOwnedDataPath();
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
        lock (_gate)
        {
            _host = host;
        }

        await WaitForReadinessAsync(invocation.Completion, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _isStarted = true;
        }
    }

    private async Task WaitForReadinessAsync(Task program, CancellationToken cancellationToken)
    {
        Uri admin = ResourceContext.Endpoints[AdminEndpointName];
        using var client = new HttpClient { BaseAddress = admin };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (program.IsCompleted)
            {
                await program.ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The resource program exited before its Database control plane reported ready.");
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
                // The admin listener has not bound yet; retry within the startup budget.
            }

            await Task.Delay(_probeInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequestStopAsync(Task program, CancellationToken cancellationToken)
    {
        Uri admin = ResourceContext.Endpoints[AdminEndpointName];
        using var client = new HttpClient { BaseAddress = admin };

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
                    $"The Database control plane rejected graceful stop with HTTP {(int)response.StatusCode}.");
            }
            catch (HttpRequestException) when (!program.IsCompleted)
            {
                await Task.Delay(_probeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (program.IsCompleted)
            {
                return;
            }
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

    private static MethodInfo ResolveEntryPoint<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)] TProgram>()
    {
        Type programType = typeof(TProgram);
        MethodInfo entryPoint = programType.Assembly.EntryPoint
            ?? throw new InvalidOperationException(
                $"Assembly '{programType.Assembly.GetName().Name}' has no executable entry point.");

        if (!ReferenceEquals(entryPoint.DeclaringType, programType))
        {
            throw new InvalidOperationException(
                $"Type '{programType.FullName}' is not the entry-point Program for assembly " +
                $"'{programType.Assembly.GetName().Name}'.");
        }
        if (!entryPoint.IsStatic)
        {
            throw new InvalidOperationException("The resource program entry point must be static.");
        }

        ParameterInfo[] parameters = entryPoint.GetParameters();
        if (parameters.Length > 1 ||
            (parameters.Length == 1 && parameters[0].ParameterType != typeof(string[])))
        {
            throw new InvalidOperationException(
                "The resource program entry point must accept no parameters or one string[] parameter.");
        }

        Type returnType = entryPoint.ReturnType;
        if (returnType != typeof(void) &&
            returnType != typeof(int) &&
            returnType != typeof(Task) &&
            returnType != typeof(Task<int>))
        {
            throw new InvalidOperationException(
                $"The resource program entry point return type '{returnType.FullName}' is not supported.");
        }

        return entryPoint;
    }

    private static (ResourceContext Context, string DataPath) CreateDefaultContext(Type programType)
    {
        string dataPath = Path.Combine(
            Path.GetTempPath(),
            "cohesion-database-testing",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);

        int databasePort = ReservePort();
        int adminPort;
        do
        {
            adminPort = ReservePort();
        }
        while (adminPort == databasePort);

        var endpoints = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
        {
            [DatabaseEndpointName] = Uri.CreateEndpoint("cohesion-db", "127.0.0.1", databasePort),
            [AdminEndpointName] = Uri.CreateEndpoint("http", "127.0.0.1", adminPort),
        };
        var mounts = new Dictionary<string, ResourceMount>(StringComparer.OrdinalIgnoreCase)
        {
            [DataMountName] = new ResourceMount(dataPath),
        };

        string resourceName = programType.Assembly.GetName().Name ?? programType.Name;
        string bootstrapCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return (
            new ResourceContext(
                applicationName: "tests",
                resourceName: resourceName,
                environmentName: "Testing",
                gatewayName: "inprocess",
                contentRootPath: AppContext.BaseDirectory,
                endpoints: endpoints,
                mounts: mounts,
                bootstrapCredential: Encoding.UTF8.GetBytes(bootstrapCredential)),
            dataPath);
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

    private void AuthorizeControlPlaneRequest(HttpRequestMessage request)
    {
        if (!ResourceContext.BootstrapCredential.IsEmpty)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                Encoding.UTF8.GetString(ResourceContext.BootstrapCredential.Span));
        }
    }

    private void DeleteOwnedDataPath()
    {
        if (_ownedDataPath is null || !Directory.Exists(_ownedDataPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_ownedDataPath, recursive: true);
        }
        catch (IOException)
        {
            // A failed assertion should not be hidden by best-effort fixture cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed assertion should not be hidden by best-effort fixture cleanup.
        }
    }
}
