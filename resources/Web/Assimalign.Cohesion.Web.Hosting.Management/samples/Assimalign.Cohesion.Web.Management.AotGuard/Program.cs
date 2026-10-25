using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Resources.Internal;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Management;

namespace Assimalign.Cohesion.Web.Management.AotGuard;

internal static class Program
{
    public static async Task<int> Main()
    {
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "https" });
        CorsPolicyStore cors = CorsPolicyStore.Create(new CorsConfiguration());
        plane.RegisterStateHandler(CorsResourceStateHandler.Create(cors));
        using X509Certificate2 initial = GuardCertificateResolver.CreateCertificate();
        using CertificateResourceStateHandler certificate = CertificateResourceStateHandler.Create(new GuardCertificateResolver(), initial);
        plane.RegisterStateHandler(certificate);
        plane.ValidateStateHandlers();
        var tls = new TlsServerOptions();
        certificate.ConfigureTls(tls);
        string original = tls.AuthenticationOptions.ServerCertificateSelectionCallback!(null!, "localhost")!.GetCertHashString();
        Guid authority = Guid.NewGuid();
        using JsonDocument corsJson = JsonDocument.Parse("""{"defaultPolicy":{"origins":["https://portal.example"]}}""");
        using JsonDocument certificateJson = JsonDocument.Parse("""{"certificateRef":{"provider":"secrets","key":"api-tls","version":"v2"}}""");
        foreach ((ResourceStateName name, ResourceStateKey key, JsonElement desired) in new[]
        {
            ((ResourceStateName)"web.cors", (ResourceStateKey)"configuration", corsJson.RootElement),
            ((ResourceStateName)"web.tls.certificate", (ResourceStateKey)"https", certificateJson.RootElement)
        })
        {
            string digest = ResourceStateWire.ComputeValueDigest(true, desired);
            ResourceStateApplyResult result = await plane.ApplyStateAsync(new ResourceStateApplyContext(authority, plane.ProcessStartId, 1, digest, 1), name, key, new ResourceStateValue(true, desired));
            if (result.Status != ResourceStateApplyStatus.Applied) { return 1; }
            ResourceStateObservation observed = await plane.ReadStateAsync(name, key);
            byte[] wire = JsonSerializer.SerializeToUtf8Bytes(observed, ResourceStateJsonContext.Default.ResourceStateObservation);
            if (JsonSerializer.Deserialize(wire, ResourceStateJsonContext.Default.ResourceStateObservation)?.ValueDigest != digest) { return 2; }
        }
        if (cors.ReadState().Configuration.DefaultPolicy!.Origins[0] != "https://portal.example" ||
            original == tls.AuthenticationOptions.ServerCertificateSelectionCallback!(null!, "localhost")!.GetCertHashString()) { return 3; }
        Console.WriteLine("Runtime manifest, CORS state and certificate selection passed.");
        return 0;
    }

}

internal sealed class GuardCertificateResolver : IResourceCertificateResolver
{
    public ValueTask<X509Certificate2> ResolveAsync(ResourceCertificateReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateCertificate());
    }

    public static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
