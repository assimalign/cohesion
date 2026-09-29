using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.SecretStore.Hosting;

// The shared ApplicationModel namespace also declares a ResourceMount; the host context takes the runtime one.
using HostingResourceMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Runs one real SecretStore host (application <c>appa</c>, resource <c>secrets</c>, Local,
/// loopback HTTP) for a test class, the way a gateway-managed store runs: every
/// <c>/cohesion/v1</c> call must carry an ES256 bearer credential signed by the application trust
/// key with the store as audience.
/// </summary>
public sealed class SecretStoreHostFixture : IAsyncLifetime
{
    internal const string Application = "appa";
    internal const string Store = "secrets";
    internal const string SeededPath = "app/api-key";

    private readonly TestTrustIdentity _identity = new(Application, "local");
    private SecretStoreApplication? _application;
    private string? _dataPath;
    private Uri? _controlPlaneAddress;

    internal static byte[] SeededValue => Encoding.UTF8.GetBytes("orchestration-secret");

    internal TestTrustIdentity Identity => _identity;

    internal Uri ControlPlaneAddress =>
        _controlPlaneAddress ?? throw new InvalidOperationException("The SecretStore host has not started.");

    /// <summary>
    /// The owner the store accepts for a trust grant made with <see cref="CreateConnection"/>'s
    /// default credential: <c>&lt;issuer&gt;@&lt;subject&gt;</c>, the shape of a model's owner.
    /// </summary>
    internal string Owner => _identity.Issuer + "@" + _identity.Subject;

    internal ResourceProviderConnection CreateConnection(string? subject = null) =>
        new(
            (ApplicationName)Application,
            (ResourceName)Store,
            "SecretStore",
            ControlPlaneAddress,
            _identity.Issue(Store, subject),
            ServerCertificateValidator: null);

    public async Task InitializeAsync()
    {
        _dataPath = Path.Combine(
            Path.GetTempPath(),
            "cohesion-secretstore-orchestration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);

        Uri endpoint = GetLoopbackEndpoint();
        _controlPlaneAddress = new Uri(endpoint, "/cohesion/v1");
        var context = new ResourceContext(
            Application,
            Store,
            AppEnvironment.Keys.Local,
            "local",
            _dataPath,
            new Dictionary<string, Uri> { ["api"] = endpoint },
            new Dictionary<string, HostingResourceMount> { ["data"] = new HostingResourceMount(_dataPath) },
            settings: null,
            references: null,
            Encoding.ASCII.GetBytes(_identity.Issue(Store)),
            _identity.PublicKey,
            ambientValues: null);

        using (ResourceRuntime.CreateScope(context))
        {
            SecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder([]);
            builder.AddSecret(SeededPath, SeededValue);
            _application = builder.Build();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await ((IHost)_application).StartAsync(timeout.Token);
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_application is not null)
            {
                await ((IHost)_application).StopAsync(CancellationToken.None);
                await ((IAsyncDisposable)_application).DisposeAsync();
            }
        }
        finally
        {
            _identity.Dispose();
            if (_dataPath is not null && Directory.Exists(_dataPath))
            {
                Directory.Delete(_dataPath, recursive: true);
            }
        }
    }

    private static Uri GetLoopbackEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
    }
}
