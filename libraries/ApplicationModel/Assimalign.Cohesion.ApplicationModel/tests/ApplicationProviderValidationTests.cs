using System;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// Drives the provider-registration rules through <see cref="IApplicationBuilder.Build"/>, which
/// validates the registrations against the built graph before the gateway sees the model.
/// </summary>
public sealed class ApplicationProviderValidationTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a bound store source builds")]
    public void Build_BoundStoreSource_ShouldSucceed()
    {
        // Arrange
        var gateway = new FakeGateway();
        IApplicationBuilder builder = CreateBuilder(gateway);
        builder.AddResource(Store("secrets", "KeyStore"));
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "secrets:db-password")));
        builder.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        gateway.ValidatedModel.ShouldBeSameAs(model);
        Should.NotThrow(() => ApplicationProviderValidation.Validate(model));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a missing binding fails Build() before the gateway validates")]
    public void Build_MissingStoreBinding_ShouldNameSourceAndOrchestrationVerb()
    {
        // Arrange
        var gateway = new FakeGateway();
        IApplicationBuilder builder = CreateBuilder(gateway);
        builder.AddResource(Store("secrets", "KeyStore"));
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "secrets:db-password")));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'secrets:db-password'", Case.Sensitive);
        exception.Message.ShouldContain("source 'secrets'", Case.Sensitive);
        exception.Message.ShouldContain("Assimalign.Cohesion.KeyStore.ApplicationModel.Orchestration", Case.Sensitive);
        exception.Message.ShouldContain("builder.UseKeyStore(...)", Case.Sensitive);
        gateway.ValidatedModel.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a missing binding for a non-model source names the Sources registration")]
    public void Build_MissingNonModelBinding_ShouldNameSourcesRegistration()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api", Mount("settings", ResourceMountKind.Configuration, "vault:api")));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("source 'vault'", Case.Sensitive);
        exception.Message.ShouldContain("builder.Providers.Sources[\"vault\"]", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a non-model source builds without a resource")]
    public void Build_NonModelSourceWithoutResourceKind_ShouldSucceed()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "vault:db-password")));
        builder.Providers.Sources["vault"] = new SecretOnlySourceProvider();

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        Should.NotThrow(() => ApplicationProviderValidation.Validate(model));
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: parameter and literal sources need no provider")]
    [InlineData(ResourceMountKind.Secret, "parameter:db-password")]
    [InlineData(ResourceMountKind.Configuration, "parameter:settings")]
    [InlineData(ResourceMountKind.Configuration, "literal:{\"a\":1}")]
    public void Build_BuiltInSource_ShouldNotRequireProvider(ResourceMountKind kind, string source)
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api", Mount("input", kind, source)));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Providers.Sources.ShouldBeEmpty();
        Should.NotThrow(() => ApplicationProviderValidation.Validate(model));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a mount without a source needs no provider")]
    public void Build_MountWithoutSource_ShouldNotRequireProvider()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api", Mount("tls", ResourceMountKind.Secret, source: null)));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        Should.NotThrow(() => ApplicationProviderValidation.Validate(model));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a source provider kind must match the bound resource kind")]
    public void Build_SourceKindMismatch_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Store("secrets", "SettingStore"));
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "secrets:db-password")));
        builder.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("kind 'KeyStore'", Case.Sensitive);
        exception.Message.ShouldContain("resource 'secrets' is kind 'SettingStore'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: resource kinds compare case-insensitively")]
    public void Build_SourceKindDifferentCase_ShouldSucceed()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Store("secrets", "KeyStore"));
        builder.Providers.Sources["secrets"] = new SecretOnlySourceProvider("keystore");

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        Should.NotThrow(() => ApplicationProviderValidation.Validate(model));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a source provider bound to a missing resource is rejected")]
    public void Build_SourceBoundToMissingResource_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "secrets:db-password")));
        builder.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("bound to resource 'secrets'", Case.Sensitive);
        exception.Message.ShouldContain("not a resource of application 'appa'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a certificate authority bound to a missing resource is rejected")]
    public void Build_CertificateAuthorityMissingResource_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api"));
        builder.Providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>(
            "authority",
            new FakeCertificateAuthority());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("certificate authority", Case.Sensitive);
        exception.Message.ShouldContain("'authority'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a trust store kind must match its bound resource")]
    public void Build_TrustStoreKindMismatch_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Store("secrets", "SettingStore"));
        builder.Providers.TrustStore = new ResourceProviderBinding<ITrustedIssuerStore>(
            "secrets",
            new FakeTrustedIssuerStore("KeyStore"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("trust store", Case.Sensitive);
        exception.Message.ShouldContain("resource 'secrets' is kind 'SettingStore'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a kind-requiring provider bound to no resource is rejected")]
    public void Build_KindRequiringProviderWithoutResource_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api"));
        builder.Providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>(
            null,
            new FakeCertificateAuthority("KeyStore"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("binding names no resource", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: providers outside the model and matching bindings build")]
    public void Build_ValidBindings_ShouldSucceed()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Store("secrets", "KeyStore"));
        IApplicationResourceDescriptor sink = builder.AddResource(Consumer("logs"));
        builder.Providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>(
            "secrets",
            new FakeCertificateAuthority("KeyStore"));
        builder.Providers.TrustStore = new ResourceProviderBinding<ITrustedIssuerStore>(
            null,
            new FakeTrustedIssuerStore());
        builder.Providers.Telemetry = ResourceTelemetrySink.FromResource(sink, "control");

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Providers.CertificateAuthority.ShouldNotBeNull().Resource.ShouldBe((ResourceName)"secrets");
        Should.NotThrow(() => ApplicationProviderValidation.Validate(model));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a telemetry sink must declare its endpoint")]
    public void Build_TelemetrySinkWithoutEndpoint_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor sink = builder.AddResource(Consumer("logs"));
        builder.Providers.Telemetry = ResourceTelemetrySink.FromResource(sink);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("telemetry sink 'logs' declares no 'otlp' endpoint", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a source referenced in another application is rejected")]
    public void Build_CrossApplicationReferenceSource_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "platform-secrets:db-password")) with
        {
            References = [Reference("platform-secrets", "platform")],
        });
        builder.Providers.Sources["platform-secrets"] = new SecretOnlySourceProvider();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'platform-secrets'", Case.Sensitive);
        exception.Message.ShouldContain("application 'platform'", Case.Sensitive);
        exception.Message.ShouldContain("Cross-application store sources are not supported yet", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a source naming a remote reference is rejected")]
    public void Build_RemoteReferenceSource_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "vault-store:db-password")));
        builder.RemoteReference("vault-store", remote => remote.Endpoint("control", "https://vault.example.test:7443"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'vault-store'", Case.Sensitive);
        exception.Message.ShouldContain("Cross-application store sources are not supported yet", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a source referenced in the declaring application builds")]
    public void Build_SameApplicationReferenceSource_ShouldSucceed()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Store("secrets", "KeyStore"));
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "secrets:db-password")) with
        {
            References = [Reference("secrets", "appa")],
        });
        builder.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        Should.NotThrow(() => ApplicationProviderValidation.Validate(model));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a source naming another application's external is rejected")]
    public void Build_ExternalManifestSource_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api", Mount("db", ResourceMountKind.Secret, "platform-secrets:db-password")));
        AddPlatformSecretsExternal(builder);
        builder.Providers.Sources["platform-secrets"] = new SecretOnlySourceProvider("KeyStore");

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'platform-secrets'", Case.Sensitive);
        exception.Message.ShouldContain("application 'platform'", Case.Sensitive);
        exception.Message.ShouldContain("Cross-application store sources are not supported yet", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a source provider registered for another application's resource is rejected")]
    public void Build_SourceBindingToExternal_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api"));
        AddPlatformSecretsExternal(builder);
        builder.Providers.Sources["platform-secrets"] = new SecretOnlySourceProvider();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("mount source 'platform-secrets'", Case.Sensitive);
        exception.Message.ShouldContain("application 'platform'", Case.Sensitive);
        exception.Message.ShouldContain("Cross-application providers are not supported yet", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a provider bound to another application's resource is rejected")]
    public void Build_BindingToRemoteReference_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api"));
        builder.RemoteReference("vault-store", remote => remote.Endpoint("control", "https://vault.example.test:7443"));
        builder.Providers.TrustStore = new ResourceProviderBinding<ITrustedIssuerStore>(
            "vault-store",
            new FakeTrustedIssuerStore());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("application 'vault-store'", Case.Sensitive);
        exception.Message.ShouldContain("Cross-application providers are not supported yet", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a registration made after Build() never reaches the built model")]
    public void Build_RegistrationAfterBuild_ShouldNotBeValidatedOrFrozen()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api"));
        IApplicationModel model = builder.Build().Model;

        // Act: an unbound source registered afterwards would fail validation, but the built
        // model holds the snapshot taken at Build().
        builder.Providers.Sources["late"] = new SecretOnlySourceProvider("KeyStore");

        // Assert
        model.Providers.Sources.ShouldBeEmpty();
        Should.NotThrow(() => ApplicationProviderValidation.Validate(model));
        Should.Throw<InvalidOperationException>(() => builder.Build())
            .Message.ShouldContain("bound to resource 'late'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a null model is rejected")]
    public void Validate_NullModel_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => ApplicationProviderValidation.Validate(null!));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a command whose manifest requires an input resolver fails Build() without one")]
    public void Build_CommandRequiringInputResolverWithoutResolver_ShouldNamePackageAndVerb()
    {
        // Arrange
        var gateway = new FakeGateway();
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(ResolvingStore("secrets", requiresInputResolver: true));
        DeclareResolveCommand(builder, store);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("command 'test.resolve' (key 'tls')", Case.Sensitive);
        exception.Message.ShouldContain("resource 'secrets'", Case.Sensitive);
        exception.Message.ShouldContain("Assimalign.Cohesion.KeyStore.ApplicationModel.Orchestration", Case.Sensitive);
        exception.Message.ShouldContain("builder.UseKeyStore(...)", Case.Sensitive);
        exception.Message.ShouldContain("builder.Providers.CommandInputs", Case.Sensitive);
        gateway.ValidatedModel.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a registered input resolver satisfies a command that requires one")]
    public void Build_CommandRequiringInputResolverWithResolver_ShouldSucceed()
    {
        // Arrange
        var gateway = new FakeGateway();
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(ResolvingStore("secrets", requiresInputResolver: true));
        DeclareResolveCommand(builder, store);
        builder.Providers.CommandInputs.Add(new FakeCommandInputResolver());

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        gateway.ValidatedModel.ShouldBeSameAs(model);
        model.Providers.CommandInputs.ShouldHaveSingleItem().CommandKind.ShouldBe("test.resolve");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a command whose manifest does not require an input resolver builds without one")]
    public void Build_CommandNotRequiringInputResolver_ShouldSucceedWithoutResolver()
    {
        // Arrange
        var gateway = new FakeGateway();
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(ResolvingStore("secrets", requiresInputResolver: false));
        DeclareResolveCommand(builder, store);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        gateway.ValidatedModel.ShouldBeSameAs(model);
        model.Providers.CommandInputs.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a set member missing an input resolver is told to register it in its callback")]
    public void Validate_SetMemberCommandRequiringInputResolver_ShouldNameSetCallback()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(ResolvingStore("secrets", requiresInputResolver: true));
        DeclareResolveCommand(builder, store);
        builder.Providers.CommandInputs.Add(new FakeCommandInputResolver());
        var member = new ProviderlessApplicationModel(builder.Build().Model);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ApplicationProviderValidation.Validate(member, applicationSetMember: true));

        // Assert
        exception.Message.ShouldContain("command 'test.resolve'", Case.Sensitive);
        exception.Message.ShouldContain(
            "set.AddApplication(Applications.<Member>, application => application.UseKeyStore(\"secrets\"))",
            Case.Sensitive);
        exception.Message.ShouldContain("application.Providers.CommandInputs", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a command for another application's resource that requires an input resolver names the resolver registration")]
    public void Build_RemoteCommandRequiringInputResolverWithoutResolver_ShouldNameCommandInputs()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api"));
        IApplicationResourceDescriptor remote = AddRemoteResolvingStore(builder);
        DeclareResolveCommand(builder, remote);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'platform-secrets' is a resource of application 'platform'", Case.Sensitive);
        exception.Message.ShouldContain("builder.Providers.CommandInputs", Case.Sensitive);
        exception.Message.ShouldNotContain("UseKeyStore", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: the declaring application's input resolver satisfies a command for another application's resource that requires one")]
    public void Build_RemoteCommandRequiringInputResolverWithResolver_ShouldSucceed()
    {
        // Arrange
        var gateway = new FakeGateway();
        IApplicationBuilder builder = CreateBuilder(gateway);
        builder.AddResource(Consumer("api"));
        IApplicationResourceDescriptor remote = AddRemoteResolvingStore(builder);
        DeclareResolveCommand(builder, remote);
        builder.Providers.CommandInputs.Add(new FakeCommandInputResolver());

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        gateway.ValidatedModel.ShouldBeSameAs(model);
        model.Providers.CommandInputs.ShouldHaveSingleItem().CommandKind.ShouldBe("test.resolve");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a set member missing an input resolver for another application's resource is told to add one to application.Providers.CommandInputs")]
    public void Validate_SetMemberRemoteCommandRequiringInputResolver_ShouldNameApplicationCommandInputs()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(Consumer("api"));
        IApplicationResourceDescriptor remote = AddRemoteResolvingStore(builder);
        DeclareResolveCommand(builder, remote);
        builder.Providers.CommandInputs.Add(new FakeCommandInputResolver());
        var member = new ProviderlessApplicationModel(builder.Build().Model);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ApplicationProviderValidation.Validate(member, applicationSetMember: true));

        // Assert
        exception.Message.ShouldContain("'platform-secrets' is a resource of application 'platform'", Case.Sensitive);
        exception.Message.ShouldContain(
            "IResourceCommandInputResolver for 'test.resolve' to application.Providers.CommandInputs", Case.Sensitive);
        exception.Message.ShouldNotContain("UseKeyStore", Case.Sensitive);
        exception.Message.ShouldNotContain("set.AddApplication", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Provider validation: a resolver registered for another command kind does not satisfy a command that requires one")]
    [InlineData("test.other")]
    [InlineData("TEST.RESOLVE")]
    public void Build_CommandRequiringInputResolverWithResolverForOtherKind_ShouldThrow(string registeredKind)
    {
        // Arrange
        var gateway = new FakeGateway();
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor store = builder.AddResource(ResolvingStore("secrets", requiresInputResolver: true));
        DeclareResolveCommand(builder, store);
        builder.Providers.CommandInputs.Add(new FakeCommandInputResolver(registeredKind));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("no IResourceCommandInputResolver is registered for 'test.resolve'", Case.Sensitive);
        exception.Message.ShouldContain("builder.UseKeyStore(...)", Case.Sensitive);
        gateway.ValidatedModel.ShouldBeNull();
    }

    private static IApplicationResourceDescriptor AddRemoteResolvingStore(IApplicationBuilder builder)
    {
        ResourceManifest manifest = ResolvingStore("platform-secrets", requiresInputResolver: true) with
        {
            Application = "platform",
        };
        var declaration = new ExternalResourceDeclaration(
            "platform-secrets",
            "platform",
            ["control"],
            optional: false,
            manifest);
        return builder.RemoteReference(
            declaration,
            reference => reference.Endpoint("control", "https://platform.example.test:7443"));
    }

    private static IApplicationBuilder CreateBuilder() => CreateBuilder(new FakeGateway());

    private static IApplicationBuilder CreateBuilder(FakeGateway gateway) => Application
        .CreateBuilder(ApplicationName.Parse("appa"), [])
        .UseGateway(gateway);

    private static ResourceManifest Store(string name, string kind) =>
        TestManifestFactory.Create(name) with { Kind = kind };

    private static ResourceManifest ResolvingStore(string name, bool requiresInputResolver) =>
        TestManifestFactory.Create(name) with
        {
            Kind = "KeyStore",
            Commands = [new ResourceManifestCommand("test.resolve") { RequiresInputResolver = requiresInputResolver }],
        };

    private static void DeclareResolveCommand(IApplicationBuilder builder, IApplicationResourceDescriptor target)
    {
        using JsonDocument payload = JsonDocument.Parse("{\"path\":\"tls\",\"source\":\"parameter:tls\"}");
        builder.AddCommand(ResourceCommands.Create(
            "test.resolve", "tls", target.Resource, ApplicationName.Parse("appa"), payload.RootElement,
            CommandTestJsonContext.Default.JsonElement));
    }

    private static ResourceManifest Consumer(string name, params ResourceManifestMount[] mounts) =>
        TestManifestFactory.Create(name) with { Mounts = mounts };

    private static ResourceManifestMount Mount(string name, ResourceMountKind kind, string? source) => new()
    {
        Name = name,
        Kind = kind,
        ContainerPath = $"/cohesion/mounts/{name}",
        Source = source,
    };

    private static void AddPlatformSecretsExternal(IApplicationBuilder builder)
    {
        ResourceManifest manifest = TestManifestFactory.Create("platform-secrets", application: "platform") with
        {
            Kind = "KeyStore",
        };
        var declaration = new ExternalResourceDeclaration(
            "platform-secrets",
            "platform",
            ["control"],
            optional: false,
            manifest);
        builder.RemoteReference(
            declaration,
            remote => remote.Endpoint("control", "https://platform.example.test:7443"));
    }

    private static ResourceManifestReference Reference(string resource, string application) => new()
    {
        Resource = resource,
        Application = application,
        Endpoints = ["control"],
        Manifest = $"Example.{application}.{resource}.Manifest",
    };
}
