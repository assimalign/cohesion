using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class LocalPortStoreTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local port store: control-plane port is loopback, stable, and persisted")]
    public async Task ResolveControlPlaneAsync_NewApplication_AllocatesStablePersistedLoopbackPort()
    {
        string root = Directory.CreateTempSubdirectory("cohesion-gateway-ports-").FullName;
        try
        {
            string stateDirectory = Path.Combine(root, ".cohesion");
            ApplicationName application = ApplicationName.Parse("port-store-tests");
            var store = new LocalPortStore(stateDirectory);

            int firstPort = await store.ResolveControlPlaneAsync(application, CancellationToken.None);
            int repeatedPort = await store.ResolveControlPlaneAsync(application, CancellationToken.None);
            int reloadedPort = await new LocalPortStore(stateDirectory)
                .ResolveControlPlaneAsync(application, CancellationToken.None);

            firstPort.ShouldBeInRange(1, 65535);
            repeatedPort.ShouldBe(firstPort);
            reloadedPort.ShouldBe(firstPort);

            IReadOnlyList<ResourceEndpoint> resourceEndpoints = await store.ResolveAsync(
                application,
                "api",
                [new ResourceEndpoint("http", "http", 8080)],
                new Dictionary<string, string>(StringComparer.Ordinal),
                CancellationToken.None);
            resourceEndpoints[0].Port.ShouldNotBe(firstPort);
            await store.DeleteAsync(application, "api", CancellationToken.None);

            string path = Path.Combine(
                stateDirectory,
                application.ToString(),
                ".state",
                "ports.json");
            File.Exists(path).ShouldBeTrue();
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path)))
            {
                document.RootElement.GetProperty("controlPlane").GetInt32().ShouldBe(firstPort);
            }

            var listener = new TcpListener(IPAddress.Loopback, firstPort);
            try
            {
                listener.Start();
                IPEndPoint endpoint = listener.LocalEndpoint.ShouldBeOfType<IPEndPoint>();
                endpoint.Address.ShouldBe(IPAddress.Loopback);
                endpoint.Port.ShouldBe(firstPort);
            }
            finally
            {
                listener.Stop();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
