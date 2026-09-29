using System;
using System.IO;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ResourceCommandTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Commands: canonical property ordering determines identity")]
    public void Create_WithReorderedPayload_ShouldKeepIdentityAndSnapshot()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResource target = AddTarget(builder).Resource;

        // Act
        IResourceCommand first = CreateCommand(target, "{\"b\":2,\"a\":{\"z\":0,\"y\":1}}");
        IResourceCommand reordered = CreateCommand(target, "{\"a\":{\"y\":1,\"z\":0},\"b\":2}");
        IResourceCommand changed = CreateCommand(target, "{\"a\":1}");
        IResourceCommand otherTarget = CreateCommand(AddTarget(CreateBuilder(), "other").Resource, "{\"a\":1}");

        // Assert
        first.Id.ShouldBe(reordered.Id);
        first.Id.Length.ShouldBe(64);
        first.Id.ShouldNotBe(changed.Id);
        changed.Id.ShouldNotBe(otherTarget.Id);
        Encoding.UTF8.GetString(first.Payload.Span).ShouldBe("{\"a\":{\"y\":1,\"z\":0},\"b\":2}");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build: commands survive portable model roundtrip with graph identity")]
    public void Build_WithCommands_ShouldFreezeAndRoundTrip()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IResourceCommandDescriptor descriptor = (IResourceCommandDescriptor)AddTarget(builder);
        using JsonDocument payload = JsonDocument.Parse("{\"name\":\"orders\"}");
        descriptor.AddCommand("test.create", "orders", payload.RootElement,
            CommandTestJsonContext.Default.JsonElement, optional: true);

        // Act
        IApplicationModel model = builder.Build().Model;
        using var stream = new MemoryStream();
        ApplicationModelDocument.Create(model).Save(stream);
        stream.Position = 0;
        IApplicationModel imported = ApplicationModelDocument.Load(stream).ToModel();
        descriptor.AddCommand("test.create", "later", JsonDocument.Parse("{\"name\":\"later\"}").RootElement,
            CommandTestJsonContext.Default.JsonElement);

        // Assert
        model.Commands.Count.ShouldBe(1);
        imported.Commands.Count.ShouldBe(1);
        imported.Commands[0].Id.ShouldBe(model.Commands[0].Id);
        imported.Commands[0].Owner.ShouldBe((ApplicationName)"appa");
        imported.Commands[0].Optional.ShouldBeTrue();
        imported.Commands[0].Target.ShouldBeSameAs(imported.Resources[0]);
        ((IResourceCommandDescriptor)imported.Descriptors[0]).Commands.Count.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => ((IResourceCommandDescriptor)model.Descriptors[0])
            .AddCommand("test.create", "orders", payload.RootElement, CommandTestJsonContext.Default.JsonElement));
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Build: validates command ownership, membership, kind, key and duplicate identity")]
    [InlineData("unknown-kind", "does not accept")]
    [InlineData("foreign-target", "not owned or referenced")]
    [InlineData("foreign-owner", "declaring application")]
    [InlineData("blank-key", "nonblank")]
    [InlineData("duplicate", "duplicate command id")]
    [InlineData("bad-id", "deterministic identity")]
    [InlineData("noncanonical", "noncanonical payload")]
    [InlineData("invalid-json", "invalid JSON payload")]
    public void Build_WithInvalidCommand_ShouldReject(string scenario, string message)
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResource target = AddTarget(builder).Resource;
        IResourceCommand valid = CreateCommand(target, "{}");
        IApplicationResource selectedTarget = scenario == "foreign-target"
            ? AddTarget(CreateBuilder()).Resource : target;
        var command = new TestResourceCommand(scenario == "bad-id" ? "forged-id" : valid.Id,
            scenario == "unknown-kind" ? "test.unknown" : valid.Kind,
            scenario == "blank-key" ? " " : valid.Key, selectedTarget,
            scenario == "foreign-owner" ? (ApplicationName)"another" : valid.Owner,
            scenario == "noncanonical" ? Encoding.UTF8.GetBytes(" { } ")
                : scenario == "invalid-json" ? Encoding.UTF8.GetBytes("{") : valid.Payload, false);
        builder.AddCommand(command);
        if (scenario == "duplicate")
        {
            builder.AddCommand(command);
        }

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        error.Message.ShouldContain(message, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Commands: portable documents reject null entries with a named error")]
    public void Parse_WithNullCommand_ShouldRejectInvalidDocument()
    {
        // Arrange
        const string json = """
            {"schema":"cohesion/model/v1","application":"appa","environment":"local",
             "gateway":"default","owner":"appa@default","mode":"run","adopt":false,
             "restartOrphans":false,"resources":[],"commands":[null]}
            """;

        // Act
        InvalidDataException error = Should.Throw<InvalidDataException>(() => ApplicationModelDocument.Parse(json));

        // Assert
        error.Message.ShouldBe("Application-model commands must not contain null entries.");
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Build: rejects competing desired values or kinds for one target key")]
    [InlineData("configurationstore.set-value")]
    [InlineData("configurationstore.remove-value")]
    public void Build_WithConflictingTargetKey_ShouldRejectDesiredStateConflict(string secondKind)
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IResourceCommandDescriptor target = (IResourceCommandDescriptor)builder.AddResource(TestManifestFactory.Create("config") with
        {
            Commands = [new ResourceManifestCommand("configurationstore.set-value"), new ResourceManifestCommand("configurationstore.remove-value")],
        });
        using JsonDocument first = JsonDocument.Parse("{\"namespace\":\"orders\",\"key\":\"title\",\"value\":\"first\"}");
        using JsonDocument second = JsonDocument.Parse("{\"namespace\":\"orders\",\"key\":\"title\",\"value\":\"second\"}");
        target.AddCommand("configurationstore.set-value", "orders/title", first.RootElement, CommandTestJsonContext.Default.JsonElement);
        target.AddCommand(secondKind, "orders/title", second.RootElement, CommandTestJsonContext.Default.JsonElement);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        error.Message.ShouldContain("conflicting desired commands for ownership key 'orders/title'");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build: independent targets may own the same command key")]
    public void Build_WithMatchingKeysOnDistinctTargets_ShouldKeepBothCommands()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddCommand(CreateCommand(AddTarget(builder, "first").Resource, "{}"));
        builder.AddCommand(CreateCommand(AddTarget(builder, "second").Resource, "{}"));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Commands.Count.ShouldBe(2);
        model.Commands[0].Key.ShouldBe(model.Commands[1].Key);
        model.Commands[0].Target.ShouldNotBeSameAs(model.Commands[1].Target);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build: legacy wrappers resolve by resource identity")]
    public void Build_WithLegacyWrapper_ShouldPreserveCanonicalDependencyAndRejectImpostor()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor target = AddTarget(builder);
        IApplicationResourceDescriptor dependent = AddTarget(builder, "dependent");
        var wrapper = new LegacyDescriptorWrapper(target);
        dependent.DependsOn(wrapper);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Descriptors[1].Dependencies[0].ShouldBeSameAs(model.Descriptors[0]);
        Should.Throw<InvalidOperationException>(() => target.DependsOn(wrapper));
        IApplicationBuilder invalid = CreateBuilder();
        AddTarget(invalid);
        AddTarget(invalid, "dependent").DependsOn(new LegacyDescriptorWrapper(AddTarget(CreateBuilder())));
        Should.Throw<InvalidOperationException>(() => invalid.Build()).Message.ShouldContain("not part of the application");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Commands: remote declarations retain provider identity and claiming owner")]
    public void Build_WithRemoteTarget_ShouldRetainClaimingOwner()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        AddTarget(builder, "local");
        ResourceManifest remote = TestManifestFactory.Create("remote", "provider") with
        {
            Commands = [new ResourceManifestCommand("test.create")],
        };
        var declaration = new ExternalResourceDeclaration(remote.Name, remote.Application, ["control"], false, remote);
        IApplicationResourceDescriptor target = builder.RemoteReference(declaration, options => options.Endpoint("control", "https://provider.example"));
        builder.AddCommand(CreateCommand(target.Resource, "{}"));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Commands[0].Owner.ShouldBe((ApplicationName)"appa");
        model.Commands[0].Target.ShouldBeSameAs(target.Resource);
        ((IResourceCommandDescriptor)target).Commands.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Remote binding: exposes peer gateway address through public command routing seam")]
    public void RemoteReference_WithGateway_ShouldExposePeerControlPlaneAddress()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();

        // Act
        IApplicationResourceDescriptor descriptor = builder.RemoteReference("provider",
            options => options.Gateway("https://provider.example/cohesion"));

        // Assert
        IExternalResource external = descriptor.Resource.ShouldBeAssignableTo<IExternalResource>();
        IControlPlaneExternalResourceResolver resolver = external.Resolver
            .ShouldBeAssignableTo<IControlPlaneExternalResourceResolver>();
        resolver.ControlPlaneAddress.ShouldBe(new Uri("https://provider.example/cohesion"));
    }

    private static IApplicationBuilder CreateBuilder() => Application.CreateBuilder((ApplicationName)"appa", [])
        .UseGateway(new FakeGateway());

    private static IApplicationResourceDescriptor AddTarget(IApplicationBuilder builder, string name = "target") =>
        builder.AddResource(TestManifestFactory.Create(name) with { Commands = [new ResourceManifestCommand("test.create")] });

    private static IResourceCommand CreateCommand(IApplicationResource target, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return ResourceCommands.Create("test.create", "key", target, (ApplicationName)"appa",
            document.RootElement, CommandTestJsonContext.Default.JsonElement);
    }
}
