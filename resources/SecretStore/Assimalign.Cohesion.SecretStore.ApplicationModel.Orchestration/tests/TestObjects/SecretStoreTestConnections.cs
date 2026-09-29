using System;
using System.Net.Security;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Builds the gateway-side values the providers receive: connections to a SecretStore control
/// plane and mount-source requests.
/// </summary>
internal static class SecretStoreTestConnections
{
    internal const string BearerCredential = "bearer-token";

    internal static Uri ControlPlaneAddress { get; } = new("https://secrets.test:8443/cohesion/v1");

    internal static ResourceProviderConnection Create(
        string kind = "SecretStore",
        RemoteCertificateValidationCallback? validator = null,
        Uri? address = null) =>
        new((ApplicationName)"appa", (ResourceName)"secrets", kind, address ?? ControlPlaneAddress, BearerCredential, validator);

    internal static ResourceSourceRequest SecretRequest(
        string key,
        ResourceProviderConnection? store,
        ResourceMountKind kind = ResourceMountKind.Secret) =>
        new((ApplicationName)"appa", (ResourceName)"api", "api-key", kind, key, store);

    internal static ResourceCertificateRequest CertificateRequest(string leafName = "api-https") =>
        new((ApplicationName)"appa", (ResourceName)"api", "https", leafName, ["api.appa.internal", "127.0.0.1"]);
}
