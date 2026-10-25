using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Management.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Management.Tests;

public sealed class ResourceStateAdapterTests
{
    [Fact(DisplayName = "Cohesion Test [Web] - Managed CORS: startup discovery freezes configured baseline before overrides")]
    public async Task CreateCors_WithConfiguredStore_ShouldExposeImmutableStartupBaselineAsync()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(new CorsConfiguration());
        CorsResourceStateHandler handler = CorsResourceStateHandler.Create(store);
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http" });
        plane.RegisterStateHandler(handler);
        plane.ValidateStateHandlers();
        string initial = plane.RuntimeManifest!.InitialState[0].Desired.Value.GetRawText();
        using JsonDocument desired = JsonDocument.Parse("""{"defaultPolicy":{"origins":["https://override.example"]},"policies":{}}""");

        // Act
        await handler.ApplyAsync(new ResourceStateApplyContext(Guid.NewGuid(), Guid.NewGuid(), 1, "override", 1), "configuration", new ResourceStateValue(true, desired.RootElement));

        // Assert
        plane.RuntimeManifest.States[0].Name.Value.ShouldBe("web.cors");
        plane.RuntimeManifest.InitialState[0].Desired.Value.GetRawText().ShouldBe(initial);
        store.GetConfiguration().DefaultPolicy!.Origins.ShouldBe(["https://override.example"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Managed CORS: Applies policies and revision atomically and refuses stale deliveries")]
    public async Task ApplyCors_WithRepeatedAndStaleDelivery_ShouldPreserveLatestPolicyAsync()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(new CorsConfiguration());
        CorsResourceStateHandler handler = CorsResourceStateHandler.Create(store);
        using JsonDocument desired = JsonDocument.Parse("""{"defaultPolicy":{"origins":["https://portal.example"]},"policies":{}}""");
        var value = new ResourceStateValue(true, desired.RootElement.Clone());
        var context = new ResourceStateApplyContext(Guid.NewGuid(), Guid.NewGuid(), 42, "value-42", 1);

        // Act
        ResourceStateApplyResult applied = await handler.ApplyAsync(context, "configuration", value, CancellationToken.None);
        ResourceStateApplyResult retry = await handler.ApplyAsync(context, "configuration", value, CancellationToken.None);
        ResourceStateApplyResult stale = await handler.ApplyAsync(context with { Revision = 41 }, "configuration", value, CancellationToken.None);

        // Assert
        applied.Status.ShouldBe(ResourceStateApplyStatus.Applied);
        retry.Status.ShouldBe(ResourceStateApplyStatus.AlreadyCurrent);
        stale.Status.ShouldBe(ResourceStateApplyStatus.Stale);
        store.ReadState().Revision.ShouldBe(42);
        store.ReadState().Configuration.DefaultPolicy!.Origins.ShouldBe(["https://portal.example"]);
        Should.Throw<InvalidOperationException>(() => store.Replace(new CorsConfiguration()));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Managed CORS: Invalid wildcard credentials leave actual state unchanged")]
    public async Task ValidateCors_WithWildcardCredentials_ShouldRejectWithoutPublishingAsync()
    {
        // Arrange
        CorsPolicyStore store = CorsPolicyStore.Create(new CorsConfiguration());
        CorsResourceStateHandler handler = CorsResourceStateHandler.Create(store);
        using JsonDocument desired = JsonDocument.Parse("""{"defaultPolicy":{"allowAnyOrigin":true,"allowCredentials":true},"policies":{}}""");

        // Act
        ResourceStateValidation result = await handler.ValidateAsync("configuration", new ResourceStateValue(true, desired.RootElement), CancellationToken.None);

        // Assert
        result.IsValid.ShouldBeFalse();
        store.ReadState().Revision.ShouldBe(0);
        store.GetConfiguration().DefaultPolicy.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Managed certificate: New handshakes select the new immutable version without exposing the key")]
    public async Task ApplyCertificate_WithVersionUpdate_ShouldChangeSelectionAndRejectStaleRevisionAsync()
    {
        // Arrange
        using X509Certificate2 startup = VersionedCertificateResolver.CreateCertificate();
        var resolver = new VersionedCertificateResolver();
        using CertificateResourceStateHandler handler = CertificateResourceStateHandler.Create(resolver, startup);
        var tls = new TlsServerOptions();
        handler.ConfigureTls(tls);
        string original = tls.AuthenticationOptions.ServerCertificateSelectionCallback!(null!, "localhost")!.GetCertHashString();
        using JsonDocument desired = JsonDocument.Parse("""{"certificateRef":{"provider":"secrets","key":"api-tls-bundle","version":"13"}}""");
        var value = new ResourceStateValue(true, desired.RootElement.Clone());
        var context = new ResourceStateApplyContext(Guid.NewGuid(), Guid.NewGuid(), 42, "cert-version-13", 1);

        // Act
        ResourceStateApplyResult applied = await handler.ApplyAsync(context, "https", value, CancellationToken.None);
        ResourceStateApplyResult retry = await handler.ApplyAsync(context, "https", value, CancellationToken.None);
        ResourceStateApplyResult stale = await handler.ApplyAsync(context with { Revision = 41 }, "https", value, CancellationToken.None);
        ResourceStateObservation observed = await handler.ReadAsync("https", CancellationToken.None);

        // Assert
        applied.Status.ShouldBe(ResourceStateApplyStatus.Applied);
        retry.Status.ShouldBe(ResourceStateApplyStatus.AlreadyCurrent);
        stale.Status.ShouldBe(ResourceStateApplyStatus.Stale);
        resolver.Calls.ShouldBe(1);
        tls.AuthenticationOptions.ServerCertificateSelectionCallback!(null!, "localhost")!.GetCertHashString().ShouldNotBe(original);
        observed.Value.Value.GetProperty("certificateRef").GetProperty("version").GetString().ShouldBe("13");
        observed.Value.Value.GetRawText().ShouldNotContain("PRIVATE KEY", Case.Sensitive);
    }

}
