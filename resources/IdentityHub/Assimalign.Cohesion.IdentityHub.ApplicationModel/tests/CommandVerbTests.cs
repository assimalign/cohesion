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

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel.Tests;

/// <summary>Tests declarative command identities, payloads, and build validation.</summary>
public sealed class CommandVerbTests
{
    /// <summary>Every command uses canonical metadata and a stable identity.</summary>
    [Theory(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - Commands: deterministic canonical declarations pass Build")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_AdvertisedCommand_ShouldPreserveCanonicalIdentity(int verb)
    {
        IApplicationBuilder first = CreateBuilder();
        IIdentityHubResourceDescriptor descriptor = first.AddIdentityHub(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(descriptor, verb, false);
        IApplicationBuilder second = CreateBuilder();
        IIdentityHubResourceDescriptor copy = second.AddIdentityHub(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
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
    [Theory(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - Commands: Build rejects an unadvertised kind")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_UnadvertisedCommand_ShouldNameKind(int verb)
    {
        IApplicationBuilder builder = CreateBuilder();
        Declare(builder.AddIdentityHub(Manifest()), verb, false);
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message
            .ShouldContain($"does not accept command kind '{Kind(verb)}'", Case.Sensitive);
    }

    /// <summary>One target key cannot carry two different desired commands.</summary>
    [Theory(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - Commands: Build rejects a duplicate target key")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_DuplicateKey_ShouldNameConflict(int verb)
    {
        IApplicationBuilder builder = CreateBuilder();
        IIdentityHubResourceDescriptor descriptor = builder.AddIdentityHub(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(descriptor, verb, false);
        if (verb == 0)
        {
            // Audience has no mutable payload field. Use explicit metadata to exercise the
            // shared key check with a distinct payload, rather than the earlier duplicate-id check.
            descriptor.AddCommand(Kind(verb), "orders", "different",
                CommandTestJsonContext.Default.String);
        }
        else { Declare(descriptor, verb, true); }

        Should.Throw<InvalidOperationException>(() => builder.Build()).Message
            .ShouldContain("conflicting desired commands for ownership key", Case.Sensitive);
    }

    /// <summary>Malformed authoring arguments identify the offending parameter.</summary>
    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - Commands: reject malformed arguments")]
    public void Declare_InvalidArguments_ShouldNameParameter()
    {
        IIdentityHubResourceDescriptor descriptor = CreateBuilder().AddIdentityHub(Manifest());
        Should.Throw<ArgumentException>(() => descriptor.AddAudience("bad/name")).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => descriptor.AddClient("client", [], "credential")).ParamName.ShouldBe("audiences");
        Should.Throw<ArgumentException>(() => descriptor.AddClient("client", ["orders"], " ")).ParamName.ShouldBe("credentialSource");
    }

    private static string Kind(int verb) => verb switch
    {
        0 => "identityhub.add-audience",
        1 => "identityhub.add-client",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static string Payload(int verb) => verb switch
    {
        0 => "{\"name\":\"orders\"}",
        1 => "{\"audiences\":[\"orders\"],\"clientId\":\"client\",\"credentialSource\":\"client-credential\"}",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static void Declare(IIdentityHubResourceDescriptor descriptor, int verb, bool changed)
    {
        switch (verb)
        {
            case 0: descriptor.AddAudience("orders", optional: changed); break;
            case 1: descriptor.AddClient("client", ["orders"], changed ? "other-credential" : "client-credential"); break;
            default: throw new ArgumentOutOfRangeException(nameof(verb));
        }
    }

    private static ResourceManifest Manifest() => IdentityHubManifestFactory.Create();
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

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class CommandTestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
