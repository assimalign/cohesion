using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.ApplicationModel;
using Assimalign.Cohesion.SecretStore.Hosting;

namespace Assimalign.Cohesion.SecretStore.Client.Tests;

/// <summary>Exercises the shipped client against a real managed resource listener.</summary>
public sealed class ResourceCommandProtocolTests
{
    static ResourceCommandProtocolTests() => ResourceRuntime.RegisterControlPlane(
        typeof(ResourceCommandProtocolTests).Assembly, SecretStoreResourceControlPlane.Create);

    /// <summary>Authenticated apply, replay, refusal and deletion round-trip through the actual host.</summary>
    [Fact(DisplayName = "Cohesion Test [SecretStore.Client] - Commands: authenticated loopback apply replay refusal and delete preserve trust responses")]
    public async Task Commands_RealHost_ShouldObserveMutationsAsync()
    {
        string data = CreateDataDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var identity = new TestBootstrapIdentity();
        string token = identity.Issue("resource");
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}");
        port.Stop();
        var address = new Uri(endpoint, "/cohesion/v1");
        var context = new ResourceContext("appa", "resource", AppEnvironment.Keys.Local, "local", data,
            new Dictionary<string, Uri> { ["api"] = endpoint },
            new Dictionary<string, ResourceMount>
            {
                ["data"] = new ResourceMount(data),
                ["credential"] = ResourceMount.FromBytes("test-client-secret"u8),
            }, settings: null, references: null, Encoding.UTF8.GetBytes(token), identity.PublicKey, ambientValues: null);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        ISecretStoreApplication application = SecretStoreApplication.CreateBuilder([], typeof(ResourceCommandProtocolTests).Assembly).Build();
        ISecretStoreClient client = SecretStoreClient.CreateForControlPlane(address, new ClientCredential(token));
        ResourceCommand[] commands =
        [
            new("command-0", "secretstore.add-secret", "appa", "orders/key", Encoding.UTF8.GetBytes("{\"path\":\"orders/key\",\"source\":\"parameter:key\",\"resolvedValue\":\"Y29tbWFuZC1kYXRh\"}")),
            new("command-1", "secretstore.issue-certificate", "appa", "certificate", Encoding.UTF8.GetBytes("{\"name\":\"certificate\",\"subject\":\"CN=custom.example\",\"subjectAlternativeNames\":[\"127.0.0.1\",\"api.example\"]}")),
        ];
        try
        {
            await application.StartAsync(timeout.Token);
            foreach (ResourceCommand command in commands)
            {
                ResourceCommandObservation applied = await client.ObserveCommandAsync(command, timeout.Token);
                applied.Status.ShouldBe("Applied");
                applied.Detail.ShouldBeNull();
            }
            for (int index = 0; index < commands.Length; index++)
            {
                ResourceCommand original = commands[index];
                commands[index] = new ResourceCommand(original.Id + "-reapply", original.Kind, original.Owner, original.Key, original.Payload);
                (await client.ObserveCommandAsync(commands[index], timeout.Token)).Status.ShouldBe("Applied");
            }


            ResourceRuntime.TryGetControlPlane((Assimalign.Cohesion.Hosting.IHost)application,
                out IResourceControlPlane? plane).ShouldBeTrue();
            plane.ShouldNotBeNull();
            foreach (ResourceCommand existing in commands)
            {
                var foreign = new Assimalign.Cohesion.Hosting.Resources.ResourceCommand(
                    "foreign-direct", existing.Kind, "other", existing.Key, existing.Payload);
                ResourceCommandRejectedException conflict = await Should.ThrowAsync<ResourceCommandRejectedException>(
                    () => plane.ExecuteCommandAsync(foreign, timeout.Token).AsTask());
                conflict.Detail.ShouldContain("appa", Case.Sensitive);
                conflict.Detail.ShouldContain(existing.Key, Case.Sensitive);
            }
            ResourceCommand owned = commands[^1];
            ResourceCommandObservation refused = await client.ObserveCommandAsync(
                new ResourceCommand("foreign", owned.Kind, "other", owned.Key, owned.Payload), timeout.Token);
            refused.Status.ShouldBe("Rejected");
            refused.Detail.ShouldNotBeNull().ShouldContain("other", Case.Sensitive);
            refused.Detail.ShouldNotBeNull().ShouldContain("appa", Case.Sensitive);

            Encoding.UTF8.GetString((await client.GetSecretAsync("orders/key", timeout.Token)).Span).ShouldBe("command-data");
            string pem = await client.GetCertificateAsync("certs/certificate", timeout.Token);
            using (var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem))
            {
                certificate.Subject.ShouldContain("CN=custom.example", Case.Sensitive);
                certificate.Extensions["2.5.29.17"]!.RawData.AsSpan().IndexOf("api.example"u8).ShouldBeGreaterThanOrEqualTo(0);
            }
            using var peer = new TestBootstrapIdentity("peer");
            var grant = new ResourceCommand("trust", "cohesion.trust.add", "appa@local", "peer", peer.PublicKey);
            await client.SendCommandAsync(grant, timeout.Token);
            (await client.ObserveCommandAsync(grant, timeout.Token)).Status.ShouldBe("Applied");
            ISecretStoreClient otherPrincipal = SecretStoreClient.CreateForControlPlane(
                address, new ClientCredential(identity.Issue("resource", "other")));
            ResourceCommandObservation trustRefusal = await otherPrincipal.ObserveCommandAsync(
                new ResourceCommand("conflict", grant.Kind, "appa@other", grant.Key, grant.Payload), timeout.Token);
            trustRefusal.Status.ShouldBe("Rejected");
            trustRefusal.Detail.ShouldNotBeNull().ShouldContain("HTTP 409", Case.Sensitive);
            using var raw = new HttpClient();
            raw.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            foreach (string malformed in new[]
            {
                "{",
                """{"id":"bad","kind":"secretstore.add-secret","owner":"appa","key":"bad","payload":3}""",
                """{"id":"bad","kind":"secretstore.add-secret","owner":"appa","key":"bad","payload":"!"}""",
            })
            {
                using var malformedBody = new StringContent(malformed, Encoding.UTF8, "application/json");
                using HttpResponseMessage malformedResponse = await raw.PostAsync(
                    new Uri(address.ToString().TrimEnd('/') + "/commands"), malformedBody, timeout.Token);
                malformedResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                using JsonDocument rejection = JsonDocument.Parse(await malformedResponse.Content.ReadAsByteArrayAsync(timeout.Token));
                rejection.RootElement.GetProperty("status").GetString().ShouldBe("Rejected");
                rejection.RootElement.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
            }
            using var grantBody = new StringContent(
                "{\"id\":\"trust-check\",\"kind\":\"cohesion.trust.add\",\"owner\":\"appa@local\",\"key\":\"peer\",\"payload\":\"" +
                Convert.ToBase64String(peer.PublicKey.Span) + "\"}", Encoding.UTF8, "application/json");
            using HttpResponseMessage legacy = await raw.PostAsync(new Uri(address.ToString().TrimEnd('/') + "/commands"), grantBody, timeout.Token);
            legacy.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await legacy.Content.ReadAsByteArrayAsync(timeout.Token)).ShouldBeEmpty();

            raw.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", identity.Issue("resource", "other"));
            using var conflictingBody = new StringContent(
                "{\"id\":\"trust-conflict\",\"kind\":\"cohesion.trust.add\",\"owner\":\"appa@other\",\"key\":\"peer\",\"payload\":\"" +
                Convert.ToBase64String(peer.PublicKey.Span) + "\"}", Encoding.UTF8, "application/json");
            using HttpResponseMessage conflictResponse = await raw.PostAsync(new Uri(address.ToString().TrimEnd('/') + "/commands"), conflictingBody, timeout.Token);
            conflictResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await conflictResponse.Content.ReadAsByteArrayAsync(timeout.Token)).ShouldBeEmpty();
            using var forbiddenBody = new StringContent(
                "{\"id\":\"trust-forbidden\",\"kind\":\"cohesion.trust.add\",\"owner\":\"appa@local\",\"key\":\"peer\",\"payload\":\"" +
                Convert.ToBase64String(peer.PublicKey.Span) + "\"}", Encoding.UTF8, "application/json");
            using HttpResponseMessage forbiddenResponse = await raw.PostAsync(new Uri(address.ToString().TrimEnd('/') + "/commands"), forbiddenBody, timeout.Token);
            forbiddenResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await forbiddenResponse.Content.ReadAsByteArrayAsync(timeout.Token)).ShouldBeEmpty();

            using var envelope = new MemoryStream();
            using (var writer = new Utf8JsonWriter(envelope))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("trustKey");
                using JsonDocument publicKey = JsonDocument.Parse(peer.PublicKey);
                publicKey.RootElement.WriteTo(writer);
                writer.WriteStartArray("allowedCommandKinds");
                writer.WriteStringValue("rezolvr.add-a-record");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            await client.SendCommandAsync(new ResourceCommand("trust-restricted", grant.Kind, grant.Owner, grant.Key, envelope.ToArray()), timeout.Token);
            using JsonDocument exported = JsonDocument.Parse(await client.GetSecretAsync("trusted-issuers.json", timeout.Token));
            JsonElement storedPeer = exported.RootElement.GetProperty("issuers").EnumerateArray()
                .Single(item => item.GetProperty("issuer").GetString() == "peer");
            storedPeer.GetProperty("allowedCommandKinds")[0].GetString().ShouldBe("rezolvr.add-a-record");

            foreach (ResourceCommand command in commands.Reverse())
            {
                (await client.DeleteCommandAsync(command, timeout.Token)).Status.ShouldBe("Deleted");
            }
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using JsonDocument remaining = JsonDocument.Parse(await http.GetStringAsync(new Uri(address.ToString().TrimEnd('/') + "/commands"), timeout.Token));
            remaining.RootElement.GetProperty("commands").GetArrayLength().ShouldBe(0);
            await application.StopAsync(timeout.Token);
            await application.DisposeAsync();
            application = SecretStoreApplication.CreateBuilder([], typeof(ResourceCommandProtocolTests).Assembly).Build();
            await application.StartAsync(timeout.Token);
            using JsonDocument reloaded = JsonDocument.Parse(await client.GetSecretAsync("trusted-issuers.json", timeout.Token));
            reloaded.RootElement.GetProperty("issuers").EnumerateArray()
                .Single(item => item.GetProperty("issuer").GetString() == "peer")
                .GetProperty("allowedCommandKinds")[0].GetString().ShouldBe("rezolvr.add-a-record");
        }
        finally
        {
            await application.StopAsync(CancellationToken.None);
            await application.DisposeAsync();
            Directory.Delete(data, recursive: true);
        }
    }

    private static string CreateDataDirectory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Assimalign.Cohesion.slnx"))) { root = root.Parent; }
        string path = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "_out", "31c-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
