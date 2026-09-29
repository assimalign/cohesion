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

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Tests;

/// <summary>Tests declarative command identities, payloads, and build validation.</summary>
public sealed class CommandVerbTests
{
    /// <summary>Every command uses canonical metadata and a stable identity.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Commands: deterministic canonical declarations pass Build")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_AdvertisedCommand_ShouldPreserveCanonicalIdentity(int verb)
    {
        IApplicationBuilder first = CreateBuilder();
        ISecretStoreResourceDescriptor descriptor = first.AddSecretStore(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(descriptor, verb, false);
        IApplicationBuilder second = CreateBuilder();
        ISecretStoreResourceDescriptor copy = second.AddSecretStore(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
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
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Commands: Build rejects an unadvertised kind")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_UnadvertisedCommand_ShouldNameKind(int verb)
    {
        IApplicationBuilder builder = CreateBuilder();
        Declare(builder.AddSecretStore(Manifest()), verb, false);
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message
            .ShouldContain($"does not accept command kind '{Kind(verb)}'", Case.Sensitive);
    }

    /// <summary>One target key cannot carry two different desired commands.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Commands: Build rejects a duplicate target key")]
    [InlineData(0)]
    [InlineData(1)]
    public void Build_DuplicateKey_ShouldNameConflict(int verb)
    {
        IApplicationBuilder builder = CreateBuilder();
        ISecretStoreResourceDescriptor descriptor = builder.AddSecretStore(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(descriptor, verb, false);
        Declare(descriptor, verb, true);

        Should.Throw<InvalidOperationException>(() => builder.Build()).Message
            .ShouldContain("conflicting desired commands for ownership key", Case.Sensitive);
    }

    /// <summary>Malformed authoring arguments identify the offending parameter.</summary>
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Commands: reject malformed arguments")]
    public void Declare_InvalidArguments_ShouldNameParameter()
    {
        ISecretStoreResourceDescriptor descriptor = CreateBuilder().AddSecretStore(Manifest());
        Should.Throw<ArgumentException>(() => descriptor.AddSecret("path", "literal:forbidden")).ParamName.ShouldBe("source");
        Should.Throw<ArgumentException>(() => descriptor.AddSecret("path", "missing-prefix")).ParamName.ShouldBe("source");
        Should.Throw<ArgumentException>(() => descriptor.IssueCertificate("bad/name", "CN=api")).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => descriptor.IssueCertificate("api", " ")).ParamName.ShouldBe("subject");
    }

    private static string Kind(int verb) => verb switch
    {
        0 => "secretstore.add-secret",
        1 => "secretstore.issue-certificate",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static string Payload(int verb) => verb switch
    {
        0 => "{\"path\":\"orders/key\",\"source\":\"parameter:credential\"}",
        1 => "{\"name\":\"api\",\"subject\":\"CN=api.example\",\"subjectAlternativeNames\":[\"api.example\"]}",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static void Declare(ISecretStoreResourceDescriptor descriptor, int verb, bool changed)
    {
        switch (verb)
        {
            case 0: descriptor.AddSecret("orders/key", changed ? "parameter:next" : "parameter:credential"); break;
            case 1: descriptor.IssueCertificate("api", changed ? "CN=other.example" : "CN=api.example", ["api.example"]); break;
            default: throw new ArgumentOutOfRangeException(nameof(verb));
        }
    }

    private static ResourceManifest Manifest() => SecretStoreManifestFactory.Create();
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
