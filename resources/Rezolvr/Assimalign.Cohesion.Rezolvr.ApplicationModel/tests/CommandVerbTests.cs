using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Rezolvr.ApplicationModel.Tests;

/// <summary>Tests declarative command identities, payloads, and build validation.</summary>
public sealed class CommandVerbTests
{
    /// <summary>Every command uses canonical metadata and a stable identity.</summary>
    [Theory(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Commands: deterministic canonical declarations pass Build")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_AdvertisedCommand_ShouldPreserveCanonicalIdentity(int verb)
    {
        IApplicationBuilder first = CreateBuilder();
        IRezolvrResourceDescriptor descriptor = first.AddRezolvr(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(descriptor, verb, false);
        IApplicationBuilder second = CreateBuilder();
        IRezolvrResourceDescriptor copy = second.AddRezolvr(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(copy, verb, false);

        descriptor.DependsOn(Array.Empty<IApplicationResourceDescriptor>()).ShouldBeSameAs(descriptor);
        IResourceCommand command = first.Build().Model.Commands.Single();
        IResourceCommand repeated = second.Build().Model.Commands.Single();

        command.Kind.ShouldBe(Kind(verb));
        command.Id.ShouldBe(repeated.Id);
        command.Id.Length.ShouldBe(64);
        command.Owner.ShouldBe((ApplicationName)"appa");
        Encoding.UTF8.GetString(command.Payload.Span).ShouldBe(Payload(verb));
    }

    /// <summary>Manifest support is checked by the shared build validator.</summary>
    [Theory(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Commands: Build rejects an unadvertised kind")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_UnadvertisedCommand_ShouldNameKind(int verb)
    {
        IApplicationBuilder builder = CreateBuilder();
        Declare(builder.AddRezolvr(Manifest()), verb, false);
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message
            .ShouldContain($"does not accept command kind '{Kind(verb)}'", Case.Sensitive);
    }

    /// <summary>One target key cannot carry two different desired commands.</summary>
    [Theory(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Commands: Build rejects a duplicate target key")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_DuplicateKey_ShouldNameConflict(int verb)
    {
        IApplicationBuilder builder = CreateBuilder();
        IRezolvrResourceDescriptor descriptor = builder.AddRezolvr(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(descriptor, verb, false);
        Declare(descriptor, verb, true);

        Should.Throw<InvalidOperationException>(() => builder.Build()).Message
            .ShouldContain("conflicting desired commands for ownership key", Case.Sensitive);
    }

    /// <summary>Malformed authoring arguments identify the offending parameter.</summary>
    [Fact(DisplayName = "Cohesion Test [Rezolvr.ApplicationModel] - Commands: reject malformed arguments")]
    public void Declare_InvalidArguments_ShouldNameParameter()
    {
        IRezolvrResourceDescriptor descriptor = CreateBuilder().AddRezolvr(Manifest());
        Should.Throw<ArgumentException>(() => descriptor.AddARecord("bad/name", IPAddress.Loopback)).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => descriptor.AddARecord("api.example", IPAddress.IPv6Loopback)).ParamName.ShouldBe("address");
        Should.Throw<ArgumentException>(() => descriptor.AddCnameRecord("api.example", "bad/target")).ParamName.ShouldBe("target");
    }

    private static string Kind(int verb) => verb switch
    {
        0 => "rezolvr.add-a-record",
        1 => "rezolvr.add-cname-record",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static string Payload(int verb) => verb switch
    {
        0 => "{\"address\":\"192.0.2.1\",\"name\":\"api.example\",\"ttlSeconds\":300}",
        1 => "{\"name\":\"api.example\",\"target\":\"origin.example\",\"ttlSeconds\":300}",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static void Declare(IRezolvrResourceDescriptor descriptor, int verb, bool changed)
    {
        switch (verb)
        {
            case 0: descriptor.AddARecord("api.example", IPAddress.Parse(changed ? "192.0.2.2" : "192.0.2.1")); break;
            case 1: descriptor.AddCnameRecord("api.example", changed ? "second.example" : "origin.example"); break;
            default: throw new ArgumentOutOfRangeException(nameof(verb));
        }
    }

    private static ResourceManifest Manifest() => new ResourceManifest { Name = "dns", Kind = "Rezolvr", Application = "appa", ApplicationModel = "Assimalign.Cohesion.Rezolvr.ApplicationModel",
Artifact = new ResourceManifestArtifact { Assembly = "Example.Rezolvr", Composable = true },
Endpoints = [
new ResourceManifestEndpoint { Name = "dns", Scheme = "dns", Protocol = "udp", ContainerPort = 53 },
new ResourceManifestEndpoint { Name = "dns-tcp", Scheme = "dns", Protocol = "tcp", ContainerPort = 53 },
new ResourceManifestEndpoint { Name = "admin", Scheme = "http", Protocol = "tcp", ContainerPort = 8081 }],
ControlPlane = new ResourceManifestControlPlane { Endpoint = "admin", Path = "/cohesion/v1" },
Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment, Replicas = 1 } };
    private static IApplicationBuilder CreateBuilder() =>
        Application.CreateBuilder((ApplicationName)"appa", []).UseGateway(new CommandTestGateway());

    private sealed class CommandTestGateway : IApplicationGateway
    {
        public ResourceName Name => "test";
        public void Validate(IApplicationModel model) { }
        public Task StartAsync(IApplicationModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReconcileAsync(IApplicationModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UninstallAsync(IApplicationModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
