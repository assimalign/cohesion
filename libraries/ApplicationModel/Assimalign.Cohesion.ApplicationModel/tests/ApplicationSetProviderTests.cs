using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// Provider registrations for application-set members. A member's model is imported from its
/// describe output, which carries no providers; the set attaches exactly what the member's own
/// <c>AddApplication(..., configure)</c> callback registered and validates it with the rules
/// <see cref="IApplicationBuilder.Build"/> applies.
/// </summary>
public sealed class ApplicationSetProviderTests
{
    private static readonly string[] _setArguments = ["--mode=apply", "--gateway=set-gateway", "--environment=Development"];

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a member's registrations are frozen and attached to its imported model")]
    public async Task RunAsync_MemberWithRegistrationCallback_ShouldAttachFrozenRegistrations()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        imported.Providers.ShouldBeSameAs(ApplicationProviders.Empty);
        var provider = new SecretOnlySourceProvider("KeyStore");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(imported), member => member.Providers.Sources["secrets"] = provider);

        // Act
        await set.RunAsync();

        // Assert
        gateway.Calls.ShouldBe(["validate-batch", "reconcile-batch"]);
        IApplicationModel bound = gateway.ReconciledModels.ShouldNotBeNull().ShouldHaveSingleItem();
        bound.ShouldNotBeSameAs(imported);
        bound.Name.ShouldBe(imported.Name);
        bound.Descriptors.ShouldBeSameAs(imported.Descriptors);
        bound.Resources.ShouldBeSameAs(imported.Resources);
        bound.Manifests.ShouldBeSameAs(imported.Manifests);
        bound.Plans.ShouldBeSameAs(imported.Plans);
        bound.Owner.ShouldBe(imported.Owner);
        bound.Providers.Sources["secrets"].ShouldBeSameAs(provider);
        Should.Throw<NotSupportedException>(() => bound.Providers.Sources["vault"] = new SecretOnlySourceProvider());
        Should.Throw<InvalidOperationException>(() => bound.Providers.Telemetry = null);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: the callback runs after the member resolves and sees its resources")]
    public async Task RunAsync_RegistrationCallback_ShouldRunAfterResolutionWithMemberLookup()
    {
        // Arrange
        var events = new List<string>();
        IApplicationModel imported = ImportMember("appa");
        ApplicationName? application = null;
        ResourceManifest? store = null;
        bool unknown = true;
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(
                new ApplicationDeclaration(imported.Name, new FixedModelResolver(imported, events)),
                member =>
                {
                    events.Add("configure");
                    application = member.Application;
                    member.TryGetResourceManifest("secrets", out store).ShouldBeTrue();
                    unknown = member.TryGetResourceManifest("missing", out ResourceManifest? missing);
                    missing.ShouldBeNull();
                    member.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");
                });
        events.ShouldBeEmpty();

        // Act
        await set.RunAsync();

        // Assert
        events.ShouldBe(["resolve:appa", "configure"]);
        application.ShouldBe(ApplicationName.Parse("appa"));
        store.ShouldNotBeNull().Kind.ShouldBe("KeyStore");
        store.Application.ShouldBe(ApplicationName.Parse("appa"));
        unknown.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: an unregistered store-backed member fails naming the member and the registration")]
    public async Task RunAsync_UnregisteredStoreBackedMember_ShouldNameMemberAndRegistration()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(imported));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appa' has invalid provider registrations.", Case.Sensitive);
        exception.Message.ShouldContain("'secrets:db-password'", Case.Sensitive);
        exception.Message.ShouldContain("no provider is registered for source 'secrets'", Case.Sensitive);
        exception.Message.ShouldContain("Assimalign.Cohesion.KeyStore.ApplicationModel.Orchestration", Case.Sensitive);
        exception.Message.ShouldContain(
            "set.AddApplication(Applications.<Member>, application => application.UseKeyStore(\"secrets\"))",
            Case.Sensitive);
        exception.InnerException.ShouldBeOfType<InvalidOperationException>();
        gateway.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a callback that registers nothing still fails a store-backed member")]
    public async Task RunAsync_EmptyRegistrationCallback_ShouldFailStoreBackedMember()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(imported), _ => { });

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldContain("Application-set member 'appa'", Case.Sensitive);
        exception.Message.ShouldContain("source 'secrets'", Case.Sensitive);
        gateway.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: one member's registrations never reach another member with the same names")]
    public async Task RunAsync_RegistrationForOneMember_ShouldNotReachSameNamedMember()
    {
        // Arrange
        IApplicationModel appa = ImportMember("appa");
        IApplicationModel appb = ImportMember("appb");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(appa), member => member.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore"))
            .AddApplication(Declare(appb));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appb' has invalid provider registrations.", Case.Sensitive);
        exception.Message.ShouldContain("no provider is registered for source 'secrets'", Case.Sensitive);
        exception.Message.ShouldContain("never inherits another application's registrations", Case.Sensitive);
        gateway.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: members registering the same source name each keep their own provider")]
    public async Task RunAsync_MembersRegisteringSameSourceName_ShouldKeepEachMembersProvider()
    {
        // Arrange
        IApplicationModel appa = ImportMember("appa");
        IApplicationModel appb = ImportMember("appb");
        var appaProvider = new SecretOnlySourceProvider("KeyStore");
        var appbProvider = new SecretOnlySourceProvider("KeyStore");
        var authority = new FakeCertificateAuthority("KeyStore");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(appa), member =>
            {
                member.Providers.Sources["secrets"] = appaProvider;
                member.Providers.CertificateAuthority =
                    new ResourceProviderBinding<IResourceCertificateAuthority>("secrets", authority);
            })
            .AddApplication(Declare(appb), member => member.Providers.Sources["secrets"] = appbProvider);

        // Act
        await set.RunAsync();

        // Assert
        IReadOnlyList<IApplicationModel> models = gateway.ReconciledModels.ShouldNotBeNull();
        models.Count.ShouldBe(2);
        models[0].Providers.Sources["secrets"].ShouldBeSameAs(appaProvider);
        models[0].Providers.CertificateAuthority.ShouldNotBeNull().Provider.ShouldBeSameAs(authority);
        models[1].Providers.Sources["secrets"].ShouldBeSameAs(appbProvider);
        models[1].Providers.Sources.Count.ShouldBe(1);
        models[1].Providers.CertificateAuthority.ShouldBeNull();
        models[0].Providers.ShouldNotBeSameAs(models[1].Providers);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a binding to a resource of the wrong kind names the member")]
    public async Task RunAsync_RegistrationWithWrongKind_ShouldNameMemberApplication()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(imported), member =>
            {
                member.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");
                member.Providers.TrustStore = new ResourceProviderBinding<ITrustedIssuerStore>(
                    "api",
                    new FakeTrustedIssuerStore("KeyStore"));
            });

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appa' has invalid provider registrations.", Case.Sensitive);
        exception.Message.ShouldContain("requires a resource of kind 'KeyStore'", Case.Sensitive);
        exception.Message.ShouldContain("resource 'api' is kind 'Worker'", Case.Sensitive);
        gateway.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a binding to a resource the member does not declare names the member")]
    public async Task RunAsync_RegistrationForUnknownResource_ShouldNameMemberApplication()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(imported), member =>
            {
                member.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");
                member.Providers.Telemetry = ResourceTelemetrySink.FromResource("logs");
            });

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appa' has invalid provider registrations.", Case.Sensitive);
        exception.Message.ShouldContain("bound to resource 'logs', which is not a resource of application 'appa'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a telemetry sink named by resource attaches to the member")]
    public async Task RunAsync_TelemetrySinkByName_ShouldAttachToMember()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(imported), member =>
            {
                member.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");
                member.Providers.Telemetry = ResourceTelemetrySink.FromResource("secrets", "control");
            });

        // Act
        await set.RunAsync();

        // Assert
        ResourceTelemetrySink sink = gateway.ReconciledModels.ShouldNotBeNull()[0].Providers.Telemetry.ShouldNotBeNull();
        sink.Resource.ShouldBe((ResourceName)"secrets");
        sink.EndpointName.ShouldBe("control");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a binding to another application's resource is rejected")]
    public async Task RunAsync_RegistrationBindingAnotherApplicationsResource_ShouldRejectCrossApplication()
    {
        // Arrange
        IApplicationModel imported = ImportMemberWithPlatformExternal("appa");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(
                Declare(imported),
                member => member.Providers.Sources["platform-secrets"] = new SecretOnlySourceProvider("KeyStore"));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appa' has invalid provider registrations.", Case.Sensitive);
        exception.Message.ShouldContain("of application 'platform'", Case.Sensitive);
        exception.Message.ShouldContain("Cross-application providers are not supported yet", Case.Sensitive);
        gateway.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a failing callback is reported with the member name")]
    public async Task RunAsync_RegistrationCallbackThrows_ShouldWrapWithMemberName()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(imported), member => member.Providers.Sources["parameter"] = new SecretOnlySourceProvider());

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appa' could not register its providers:", Case.Sensitive);
        exception.Message.ShouldContain("is reserved", Case.Sensitive);
        exception.InnerException.ShouldBeOfType<ArgumentException>();
        gateway.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: registering after the callback returns throws")]
    public async Task RunAsync_RegistrationAfterCallbackReturns_ShouldThrow()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        IApplicationProviderBuilder? captured = null;
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(imported), member =>
            {
                captured = member;
                member.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");
            });
        await set.RunAsync();
        IApplicationProviderBuilder surface = captured.ShouldNotBeNull();

        // Act
        Action addSource = () => surface.Providers.Sources["vault"] = new SecretOnlySourceProvider();
        Action setIssuer = () => surface.Providers.CredentialIssuer = new FakeCredentialIssuer();

        // Assert
        Should.Throw<NotSupportedException>(addSource);
        Should.Throw<InvalidOperationException>(setIssuer).Message.ShouldContain("frozen", Case.Sensitive);
        gateway.ReconciledModels.ShouldNotBeNull()[0].Providers.Sources.ContainsKey("vault").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a model that already carries registrations is not registered twice")]
    public async Task RunAsync_ResolvedModelWithRegistrations_ShouldRejectSetRegistration()
    {
        // Arrange
        IApplicationModel built = BuildMember("appa", register: true);
        built.Providers.Sources.ContainsKey("secrets").ShouldBeTrue();
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(built), member => member.Providers.Sources["vault"] = new SecretOnlySourceProvider());

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldContain("Application-set member 'appa'", Case.Sensitive);
        exception.Message.ShouldContain("already carries provider registrations", Case.Sensitive);
        gateway.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: a built model added without a callback keeps its own registrations")]
    public async Task RunAsync_BuiltModelWithoutCallback_ShouldKeepItsRegistrations()
    {
        // Arrange
        IApplicationModel built = BuildMember("appa", register: true);
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, _setArguments)
            .AddApplication(Declare(built));

        // Act
        await set.RunAsync();

        // Assert
        gateway.ReconciledModels.ShouldNotBeNull()[0].ShouldBeSameAs(built);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application set providers: the callback overload rejects null arguments and duplicate members")]
    public void AddApplication_WithInvalidArguments_ShouldThrow()
    {
        // Arrange
        IApplicationModel imported = ImportMember("appa");
        IApplicationSet set = Application.CreateSet(new RecordingSetGateway(), _setArguments)
            .AddApplication(Declare(imported), _ => { });

        // Act
        ArgumentNullException missingCallback = Should.Throw<ArgumentNullException>(
            () => set.AddApplication(Declare(ImportMember("appb")), null!));
        ArgumentNullException missingDeclaration = Should.Throw<ArgumentNullException>(
            () => set.AddApplication(null!, _ => { }));
        InvalidOperationException duplicate = Should.Throw<InvalidOperationException>(
            () => set.AddApplication(Declare(imported), _ => { }));

        // Assert
        missingCallback.ParamName.ShouldBe("configure");
        missingDeclaration.ParamName.ShouldBe("application");
        duplicate.Message.ShouldContain("'appa' is already present", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application builder: the default builder's provider surface finds manifest-backed resources")]
    public void TryGetResourceManifest_OnDefaultBuilder_ShouldFindManifestBackedResources()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(Store("secrets", "KeyStore"));
        builder.AddResource(new FakeResource("plain"));
        IApplicationProviderBuilder surface = builder.ShouldBeAssignableTo<IApplicationProviderBuilder>();

        // Act
        bool store = surface.TryGetResourceManifest("secrets", out ResourceManifest? manifest);
        bool plain = surface.TryGetResourceManifest("plain", out ResourceManifest? plainManifest);
        bool missing = surface.TryGetResourceManifest("missing", out _);

        // Assert
        surface.Application.ShouldBe(ApplicationName.Parse("appa"));
        surface.Providers.ShouldBeSameAs(builder.Providers);
        store.ShouldBeTrue();
        manifest.ShouldNotBeNull().Kind.ShouldBe("KeyStore");
        plain.ShouldBeFalse();
        plainManifest.ShouldBeNull();
        missing.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application builder: the contract does not extend the provider-registration surface and the default builder implements both")]
    public void CreateBuilder_Unnamed_ShouldImplementBothSeparateSurfaces()
    {
        // Arrange
        Type contract = typeof(IApplicationBuilder);

        // Act
        IApplicationBuilder builder = Application.CreateBuilder([]);

        // Assert
        contract.GetInterfaces().ShouldNotContain(typeof(IApplicationProviderBuilder));
        contract.GetMember(nameof(IApplicationProviderBuilder.Application)).ShouldBeEmpty();
        contract.GetMember(nameof(IApplicationProviderBuilder.TryGetResourceManifest)).ShouldBeEmpty();
        IApplicationProviderBuilder surface = builder.ShouldBeAssignableTo<IApplicationProviderBuilder>();
        surface.Providers.ShouldBeSameAs(builder.Providers);
        surface.Application.ShouldBeNull();
    }

    private static ApplicationDeclaration Declare(IApplicationModel model) =>
        new(model.Name, new FixedModelResolver(model));

    // A member model as an application set receives it: the member's own gateway built it with its
    // registrations, and its describe output (an application-model document) carries none.
    private static IApplicationModel ImportMember(string application) =>
        ApplicationModelDocument.Create(BuildMember(application, register: true))
            .ToModel(GatewayRunMode.Apply, (ResourceName)"set-gateway");

    private static IApplicationModel BuildMember(string application, bool register)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(application),
                ["--mode=apply", "--gateway=fake", "--environment=Development"])
            .UseGateway(new FakeGateway());
        builder.AddResource(Store("secrets", "KeyStore", application));
        builder.AddResource(Consumer(
            "api",
            application,
            Mount("db", ResourceMountKind.Secret, "secrets:db-password")));
        if (register)
        {
            builder.Providers.Sources["secrets"] = new SecretOnlySourceProvider("KeyStore");
        }

        return builder.Build().Model;
    }

    private static IApplicationModel ImportMemberWithPlatformExternal(string application)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(application),
                ["--mode=apply", "--gateway=fake", "--environment=Development"])
            .UseGateway(new FakeGateway());
        ResourceManifest platformSecrets = TestManifestFactory.Create("platform-secrets", application: "platform") with
        {
            Kind = "KeyStore",
        };
        builder.RemoteReference(
            new ExternalResourceDeclaration("platform-secrets", "platform", ["control"], optional: false, platformSecrets),
            remote => remote.Endpoint("control", "https://platform.example.test:7443"));
        builder.AddResource(TestManifestFactory.Create("api", application));
        return ApplicationModelDocument.Create(builder.Build().Model)
            .ToModel(GatewayRunMode.Apply, (ResourceName)"set-gateway");
    }

    private static ResourceManifest Store(string name, string kind, string application = "appa") =>
        TestManifestFactory.Create(name, application) with { Kind = kind };

    private static ResourceManifest Consumer(string name, string application, params ResourceManifestMount[] mounts) =>
        TestManifestFactory.Create(name, application) with { Mounts = mounts };

    private static ResourceManifestMount Mount(string name, ResourceMountKind kind, string source) => new()
    {
        Name = name,
        Kind = kind,
        ContainerPath = $"/cohesion/mounts/{name}",
        Source = source,
    };
}
