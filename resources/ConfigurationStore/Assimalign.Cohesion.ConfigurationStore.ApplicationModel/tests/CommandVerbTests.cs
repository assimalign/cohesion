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

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Tests;

/// <summary>Tests declarative command identities, payloads, and build validation.</summary>
public sealed class CommandVerbTests
{
    /// <summary>Every command uses canonical metadata and a stable identity.</summary>
    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Commands: deterministic canonical declarations pass Build")]
    [InlineData(0)]
    public void Build_AdvertisedCommand_ShouldPreserveCanonicalIdentity(int verb)
    {
        IApplicationBuilder first = CreateBuilder();
        IConfigurationStoreResourceDescriptor descriptor = first.AddConfigurationStore(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(descriptor, verb, false);
        IApplicationBuilder second = CreateBuilder();
        IConfigurationStoreResourceDescriptor copy = second.AddConfigurationStore(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
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
    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Commands: Build rejects an unadvertised kind")]
    [InlineData(0)]
    public void Build_UnadvertisedCommand_ShouldNameKind(int verb)
    {
        IApplicationBuilder builder = CreateBuilder();
        Declare(builder.AddConfigurationStore(Manifest()), verb, false);
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message
            .ShouldContain($"does not accept command kind '{Kind(verb)}'", Case.Sensitive);
    }

    /// <summary>One target key cannot carry two different desired commands.</summary>
    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Commands: Build rejects a duplicate target key")]
    [InlineData(0)]
    public void Build_DuplicateKey_ShouldNameConflict(int verb)
    {
        IApplicationBuilder builder = CreateBuilder();
        IConfigurationStoreResourceDescriptor descriptor = builder.AddConfigurationStore(Manifest() with { Commands = [new ResourceManifestCommand(Kind(verb))] });
        Declare(descriptor, verb, false);
        Declare(descriptor, verb, true);

        Should.Throw<InvalidOperationException>(() => builder.Build()).Message
            .ShouldContain("conflicting desired commands for ownership key", Case.Sensitive);
    }

    /// <summary>Malformed authoring arguments identify the offending parameter.</summary>
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Commands: reject malformed arguments")]
    public void Declare_InvalidArguments_ShouldNameParameter()
    {
        IConfigurationStoreResourceDescriptor descriptor = CreateBuilder().AddConfigurationStore(Manifest());
        Should.Throw<ArgumentException>(() => descriptor.AddNamespace("bad/name")).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => descriptor.AddNamespace("orders", new Dictionary<string,string?> { ["bad/key"] = null })).ParamName.ShouldBe("seed");
    }

    private static string Kind(int verb) => verb switch
    {
        0 => "configurationstore.add-namespace",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static string Payload(int verb) => verb switch
    {
        0 => "{\"name\":\"orders\",\"seed\":{\"title\":\"Orders\"}}",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static void Declare(IConfigurationStoreResourceDescriptor descriptor, int verb, bool changed)
    {
        switch (verb)
        {
            case 0: descriptor.AddNamespace("orders", new Dictionary<string, string?> { ["title"] = changed ? "Other" : "Orders" }); break;
            default: throw new ArgumentOutOfRangeException(nameof(verb));
        }
    }

    private static ResourceManifest Manifest() => ConfigurationStoreManifestFactory.Create();
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
