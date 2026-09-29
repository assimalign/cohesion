using System;
using System.Net.Security;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Builds the requests a gateway hands the provider: an <c>api</c> resource of application
/// <c>appa</c> resolving its <c>settings</c> mount from the <c>appa-configuration</c> store.
/// </summary>
internal static class SourceRequestFactory
{
    internal const string StoreName = "appa-configuration";

    internal const string Credential = "store-token";

    internal static ResourceProviderConnection Connection(
        string controlPlaneAddress = "https://configuration.test:8443/cohesion/v1",
        string credential = Credential,
        string resourceKind = "ConfigurationStore",
        RemoteCertificateValidationCallback? validator = null) =>
        Connection(new Uri(controlPlaneAddress, UriKind.Absolute), credential, resourceKind, validator);

    internal static ResourceProviderConnection Connection(
        Uri controlPlaneAddress,
        string credential = Credential,
        string resourceKind = "ConfigurationStore",
        RemoteCertificateValidationCallback? validator = null) =>
        new(
            ApplicationName.Parse("appa"),
            (ResourceName)StoreName,
            resourceKind,
            controlPlaneAddress,
            credential,
            validator);

    internal static ResourceSourceRequest Request(
        ResourceProviderConnection? store,
        string key = "api",
        ResourceMountKind kind = ResourceMountKind.Configuration) =>
        new(
            ApplicationName.Parse("appa"),
            (ResourceName)"api",
            "settings",
            kind,
            key,
            store);
}
