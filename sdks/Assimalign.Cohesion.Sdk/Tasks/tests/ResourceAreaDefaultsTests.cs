using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Tests;

public sealed class ResourceAreaDefaultsTests
{
    [Theory(DisplayName = "Cohesion Test [Sdk] - HTTPS area defaults declare the tls Secret certificate mount")]
    [InlineData("ConfigurationStore")]
    [InlineData("IdentityHub")]
    [InlineData("LogSpace")]
    [InlineData("SecretStore")]
    public void HttpsAreaDefaults_ShouldDeclareCertificateAndSecretMount(string area)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Assimalign.Cohesion.slnx")))
        {
            root = root.Parent;
        }
        root.ShouldNotBeNull();
        XDocument defaults = XDocument.Load(Path.Combine(root.FullName, "sdks", $"Assimalign.Cohesion.Sdk.{area}", "Targets", $"Sdk.{area}.props"));
        XElement[] https = defaults.Descendants("CohesionEndpoint").Where(endpoint => (string?)endpoint.Attribute("Scheme") == "https").ToArray();
        https.ShouldNotBeEmpty();
        https.ShouldAllBe(endpoint => (string?)endpoint.Attribute("Certificate") == "tls");
        XElement mount = defaults.Descendants("CohesionMount").Single(mount => (string?)mount.Attribute("Include") == "tls");
        mount.Attribute("Kind")!.Value.ShouldBe("Secret");
        mount.Attribute("ContainerPath")!.Value.ShouldBe("/cohesion/mounts/tls");
    }

    [Theory(DisplayName = "Cohesion Test [Sdk] - Generic area defaults register their control plane and probes")]
    [InlineData("ApiManager", "http")]
    [InlineData("EmailHub", "http")]
    [InlineData("EventHub", "http")]
    [InlineData("IoTHub", "http")]
    [InlineData("LoadBalancer", "http")]
    [InlineData("LogSpace", "query")]
    [InlineData("MediaHub", "http")]
    [InlineData("MessageHub", "http")]
    [InlineData("NatGateway", "http")]
    [InlineData("NotificationHub", "http")]
    [InlineData("Rezolvr", "admin")]
    [InlineData("VpnGateway", "http")]
    public void AreaDefaults_ShouldDeclareFactoryAndMatchingProbes(string area, string endpoint)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Assimalign.Cohesion.slnx")))
        {
            root = root.Parent;
        }
        root.ShouldNotBeNull();
        XDocument defaults = XDocument.Load(Path.Combine(root.FullName, "sdks",
            $"Assimalign.Cohesion.Sdk.{area}", "Targets", $"Sdk.{area}.props"));

        XElement model = defaults.Descendants("CohesionResourceApplicationModel").Single();
        XElement factory = defaults.Descendants("CohesionResourceControlPlaneType").Single();
        model.Value.ShouldBe($"Assimalign.Cohesion.{area}.ApplicationModel");
        factory.Value.ShouldBe($"Assimalign.Cohesion.{area}.ApplicationModel.{area}ResourceControlPlane");
        model.Attribute("Condition")!.Value.ShouldBe("'$(CohesionResourceApplicationModel)' == ''");
        factory.Attribute("Condition")!.Value.ShouldBe("'$(CohesionResourceControlPlaneType)' == ''");
        defaults.Descendants("CohesionControlPlaneEndpoint").Single().Value.ShouldBe(endpoint);
        defaults.Descendants("CohesionEndpoint").ShouldContain(item => (string?)item.Attribute("Include") == endpoint);
        XElement[] probes = defaults.Descendants("CohesionProbe").ToArray();
        probes.Length.ShouldBe(2);
        foreach ((string role, string path) in new[] { ("readiness", "/readyz"), ("liveness", "/livez") })
        {
            XElement probe = probes.Single(item => (string?)item.Attribute("Include") == role);
            probe.Attribute("Endpoint")!.Value.ShouldBe(endpoint);
            probe.Attribute("Http")!.Value.ShouldBe(path);
        }
        if (area == "Rezolvr")
        {
            XElement command = defaults.Descendants("CohesionCommand").Single();
            command.Attributes().Select(static attribute => attribute.Name.LocalName).ShouldBe(["Include"]);
            command.Attribute("Include")!.Value.ShouldBe("rezolvr.add-a-record;rezolvr.add-cname-record");
        }
        else
        {
            defaults.Descendants("CohesionCommand").ShouldBeEmpty();
        }
    }
}
