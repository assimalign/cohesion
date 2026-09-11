using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Client;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

using ClientCommand = Assimalign.Cohesion.ConfigurationStore.Client.ResourceCommand;
using RuntimeCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

public sealed class ResourceCommandHostingTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Commands: direct and authenticated HTTP dispatch share ownership, values, and refusal details")]
    public async Task ObserveCommandAsync_WithGatewayScopedHost_ShouldShareDirectMutationAndDeletion()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var identity = new TestBootstrapIdentity();
            Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
            string token = identity.Issue("configuration");
            using IDisposable scope = ResourceRuntime.CreateScope(ConfigurationStoreTestHost.CreateContext(endpoint, directory, token, identity.PublicKey));
            IConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder();
            builder.AddNamespace("app", ns => ns.Set("Mode", "initial"));
            await using IConfigurationStoreApplication application = builder.Build();
            ResourceRuntime.TryGetControlPlane((IHost)application, out IResourceControlPlane? plane).ShouldBeTrue();
            plane.ShouldNotBeNull();
            await application.StartAsync(cancellation.Token);
            try
            {
                IConfigurationStoreClient read = ConfigurationStoreClient.Create(endpoint, new ClientCredential(token));
                IConfigurationStoreClient commands = ConfigurationStoreClient.CreateForControlPlane(new Uri(endpoint, "/cohesion/v1"), new ClientCredential(token));
                byte[] payload = Encoding.UTF8.GetBytes("""{"namespace":"app","key":"Mode","value":"applied"}""");
                var runtime = new RuntimeCommand("set-mode", "configurationstore.set-value", "appa", "app/Mode", payload);
                var command = new ClientCommand(runtime.Id, runtime.Kind, runtime.Owner, runtime.Key, runtime.Payload);

                // Act / Assert
                await plane.ExecuteCommandAsync(runtime, cancellation.Token);
                (await commands.ObserveCommandAsync(command, cancellation.Token)).Status.ShouldBe("Applied");
                (await read.GetNamespaceAsync("app", cancellation.Token))["Mode"].ShouldBe("applied");
                ResourceCommandRejectedException ownership = await Should.ThrowAsync<ResourceCommandRejectedException>(
                    () => plane.ExecuteCommandAsync(runtime with { Owner = "appb" }, cancellation.Token).AsTask());
                ownership.Detail.ShouldNotBeNull().ShouldContain("appa", Case.Sensitive);
                ResourceCommandObservation forbidden = await commands.ObserveCommandAsync(new ClientCommand(
                    "foreign", runtime.Kind, "appb", "app/Mode", payload), cancellation.Token);
                forbidden.Status.ShouldBe("Rejected");
                forbidden.Detail.ShouldNotBeNull().ShouldContain("authenticated issuer 'appa'", Case.Sensitive);
                ResourceCommandObservation unsupported = await commands.ObserveCommandAsync(new ClientCommand(
                    "unknown", "unknown", "appa", "app/Unknown", ReadOnlyMemory<byte>.Empty), cancellation.Token);
                unsupported.Detail.ShouldNotBeNull().ShouldContain("unknown", Case.Sensitive);
                ResourceCommandObservation missing = await commands.ObserveCommandAsync(new ClientCommand(
                    "missing", runtime.Kind, "appa", "absent/Mode", Encoding.UTF8.GetBytes("""{"value":"test"}""")), cancellation.Token);
                missing.Status.ShouldBe("Rejected");
                missing.Detail.ShouldNotBeNull().ShouldContain("namespace 'absent'", Case.Sensitive);
                var ambiguousKey = new RuntimeCommand("ambiguous-key", runtime.Kind, "appa", "app/nested/Mode",
                    Encoding.UTF8.GetBytes("""{"namespace":"app","key":"nested/Mode","value":"invalid"}"""));
                ResourceCommandRejectedException keyRefusal = await Should.ThrowAsync<ResourceCommandRejectedException>(
                    () => plane.ExecuteCommandAsync(ambiguousKey, cancellation.Token).AsTask());
                keyRefusal.Detail.ShouldContain("must not contain '/'", Case.Sensitive);
                ResourceCommandObservation keyObservation = await commands.ObserveCommandAsync(new ClientCommand(
                    ambiguousKey.Id, ambiguousKey.Kind, ambiguousKey.Owner, ambiguousKey.Key, ambiguousKey.Payload), cancellation.Token);
                keyObservation.Status.ShouldBe("Rejected");
                keyObservation.Detail.ShouldBe(keyRefusal.Detail);
                (await commands.DeleteCommandAsync(command, cancellation.Token)).Status.ShouldBe("Deleted");
                (await read.GetNamespaceAsync("app", cancellation.Token)).ContainsKey("Mode").ShouldBeFalse();
                plane.Commands.ShouldBeEmpty();
            }
            finally
            {
                await application.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
