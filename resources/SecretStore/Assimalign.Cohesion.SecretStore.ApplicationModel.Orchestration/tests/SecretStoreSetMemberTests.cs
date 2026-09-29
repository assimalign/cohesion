using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// <c>UseSecretStore(name)</c> on an application-set member. A member's model is imported from its
/// describe output, which carries no providers, so the set registers them for that member alone; a
/// real gateway then resolves the member's Secret mounts through the real SecretStore providers
/// against a loopback store double.
/// </summary>
public sealed class SecretStoreSetMemberTests
{
    private const string secretPath = "app/api-key";
    private const string secretTarget = "/cohesion/v1/secrets?path=app%2Fapi-key";

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Set member: UseSecretStore resolves each member's secret mounts through its own store")]
    public async Task UseSecretStore_OnSetMembers_ShouldResolveEachMembersSecretMounts()
    {
        // Arrange
        await using var appaStore = new LoopbackStore();
        await using var appbStore = new LoopbackStore();
        appaStore.Respond(secretTarget, "application/octet-stream", Encoding.UTF8.GetBytes("appa-secret"));
        appbStore.Respond(secretTarget, "application/octet-stream", Encoding.UTF8.GetBytes("appb-secret"));
        var controller = new StoreController(
            "secrets",
            new Dictionary<string, int> { ["appa"] = appaStore.Port, ["appb"] = appbStore.Port });
        var gateway = new StoreGateway(controller);
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(ImportMember("appa")), appa => appa.UseSecretStore("secrets"))
            .AddApplication(Declare(ImportMember("appb")), appb => appb.UseSecretStore("secrets"));

        try
        {
            // Act
            await set.RunAsync();

            // Assert
            Encoding.UTF8.GetString(controller.Inputs["appa/api"].Mounts["api-key"].Content.Span).ShouldBe("appa-secret");
            Encoding.UTF8.GetString(controller.Inputs["appb/api"].Mounts["api-key"].Content.Span).ShouldBe("appb-secret");
            LoopbackStoreRequest appaRequest = appaStore.Requests.ShouldHaveSingleItem();
            appaRequest.Method.ShouldBe("GET");
            appaRequest.Target.ShouldBe(secretTarget);
            string appaCredential = appaRequest.Authorization.ShouldNotBeNull();
            appaCredential.ShouldStartWith("Bearer ", Case.Sensitive);
            JsonWebToken token = JsonWebToken.Parse(appaCredential["Bearer ".Length..]);
            token.Audiences.ShouldBe(["secrets"]);
            token.Issuer.ShouldBe("appa");
            appbStore.Requests.ShouldHaveSingleItem().Target.ShouldBe(secretTarget);
        }
        finally
        {
            await ((IMultiModelApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Set member: a handle kept past the member's callback throws instead of writing lost registrations")]
    public async Task AsCertificateAuthority_OnMemberHandleAfterCallbackReturns_ShouldThrowFrozen()
    {
        // Arrange
        await using var appaStore = new LoopbackStore();
        appaStore.Respond(secretTarget, "application/octet-stream", Encoding.UTF8.GetBytes("appa-secret"));
        var controller = new StoreController(
            "secrets",
            new Dictionary<string, int> { ["appa"] = appaStore.Port });
        var gateway = new StoreGateway(controller);
        IApplicationProviderBuilder? member = null;
        SecretStoreProviderBuilder? handle = null;
        bool sameInCallback = false;
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(ImportMember("appa")), appa =>
            {
                member = appa;
                handle = appa.UseSecretStore("secrets");
                sameInCallback = ReferenceEquals(handle.Providers, appa.Providers);
            });

        try
        {
            await set.RunAsync();
            SecretStoreProviderBuilder kept = handle.ShouldNotBeNull();

            // Act
            Action late = () => kept.AsCertificateAuthority();

            // Assert
            sameInCallback.ShouldBeTrue();
            Should.Throw<InvalidOperationException>(late).Message.ShouldContain("frozen", Case.Sensitive);
            kept.Providers.ShouldBeSameAs(member.ShouldNotBeNull().Providers);
            controller.Models["appa"].Providers.CertificateAuthority.ShouldBeNull();
        }
        finally
        {
            await ((IMultiModelApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Set member: authority and trust-store roles attach to the registering member only")]
    public async Task UseSecretStore_WithRolesOnOneMember_ShouldAttachRolesToThatMemberOnly()
    {
        // Arrange
        await using var appaStore = new LoopbackStore();
        await using var appbStore = new LoopbackStore();
        appaStore.Respond(secretTarget, "application/octet-stream", Encoding.UTF8.GetBytes("appa-secret"));
        appbStore.Respond(secretTarget, "application/octet-stream", Encoding.UTF8.GetBytes("appb-secret"));
        var controller = new StoreController(
            "secrets",
            new Dictionary<string, int> { ["appa"] = appaStore.Port, ["appb"] = appbStore.Port });
        var gateway = new StoreGateway(controller);
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(
                Declare(ImportMember("appa")),
                appa => appa.UseSecretStore("secrets").AsCertificateAuthority().AsTrustStore())
            .AddApplication(Declare(ImportMember("appb")), appb => appb.UseSecretStore("secrets"));

        try
        {
            // Act
            await set.RunAsync();

            // Assert
            ApplicationProviders appa = controller.Models["appa"].Providers;
            appa.Sources["secrets"].ShouldBeOfType<SecretStoreSourceProvider>();
            appa.CommandInputs.ShouldHaveSingleItem().ShouldBeOfType<SecretStoreAddSecretInputResolver>();
            appa.CertificateAuthority.ShouldNotBeNull().Resource.ShouldBe((ResourceName)"secrets");
            appa.CertificateAuthority.Provider.ShouldBeOfType<SecretStoreCertificateAuthority>();
            appa.TrustStore.ShouldNotBeNull().Provider.ShouldBeOfType<SecretStoreTrustedIssuerStore>();

            ApplicationProviders appb = controller.Models["appb"].Providers;
            appb.Sources["secrets"].ShouldBeOfType<SecretStoreSourceProvider>();
            appb.Sources["secrets"].ShouldNotBeSameAs(appa.Sources["secrets"]);
            appb.CertificateAuthority.ShouldBeNull();
            appb.TrustStore.ShouldBeNull();
        }
        finally
        {
            await ((IMultiModelApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Set member: a store-backed member without UseSecretStore fails naming the member and the verb")]
    public async Task RunAsync_MemberWithoutUseSecretStore_ShouldNameMemberAndVerb()
    {
        // Arrange
        var controller = new StoreController("secrets", new Dictionary<string, int>());
        var gateway = new StoreGateway(controller);
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(ImportMember("appa")), appa => appa.UseSecretStore("secrets"))
            .AddApplication(Declare(ImportMember("appb")));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appb' has invalid provider registrations.", Case.Sensitive);
        exception.Message.ShouldContain("Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration", Case.Sensitive);
        exception.Message.ShouldContain(
            "set.AddApplication(Applications.<Member>, application => application.UseSecretStore(\"secrets\"))",
            Case.Sensitive);
        controller.Reconciled.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Set member: UseSecretStore refuses a member resource of another kind, naming the member")]
    public async Task UseSecretStore_OnSetMemberWithOtherKind_ShouldNameMember()
    {
        // Arrange
        var controller = new StoreController("secrets", new Dictionary<string, int>());
        var gateway = new StoreGateway(controller);
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(ImportMember("appa")), appa => appa.UseSecretStore("api"));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appa' could not register its providers:", Case.Sensitive);
        exception.Message.ShouldContain("Resource 'api' of application 'appa' is kind 'Web'", Case.Sensitive);
        exception.InnerException.ShouldBeOfType<ArgumentException>().ParamName.ShouldBe("store");
        controller.Reconciled.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore by name: registers on the default builder's provider surface what the descriptor overload registers")]
    public void UseSecretStore_ByNameOnBuilder_ShouldRegisterSourceAndResolver()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);
        builder.AddResource(Manifest("appa", "secrets", "SecretStore"));

        // Act
        SecretStoreProviderBuilder handle = ((IApplicationProviderBuilder)builder).UseSecretStore("secrets").AsTrustStore();

        // Assert
        handle.Providers.ShouldBeSameAs(builder.Providers);
        handle.Store.ShouldBe((ResourceName)"secrets");
        builder.Providers.Sources["secrets"].ShouldBeOfType<SecretStoreSourceProvider>();
        builder.Providers.CommandInputs.ShouldHaveSingleItem().ShouldBeOfType<SecretStoreAddSecretInputResolver>();
        builder.Providers.TrustStore.ShouldNotBeNull().Resource.ShouldBe((ResourceName)"secrets");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore by name: a known resource of another kind is refused before anything is written")]
    public void UseSecretStore_ByNameWithOtherKind_ShouldThrowAndRegisterNothing()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);
        builder.AddResource(Manifest("appa", "settings", "ConfigurationStore"));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => ((IApplicationProviderBuilder)builder).UseSecretStore("settings"));

        // Assert
        exception.ParamName.ShouldBe("store");
        exception.Message.ShouldContain("Resource 'settings' of application 'appa' is kind 'ConfigurationStore'", Case.Sensitive);
        builder.Providers.Sources.ShouldBeEmpty();
        builder.Providers.CommandInputs.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore by name: another application's resource of another kind is refused, naming its application")]
    public void UseSecretStore_ByNameForOtherApplicationsResourceOfOtherKind_ShouldNameResourceApplication()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);
        builder.RemoteReference(
            new ExternalResourceDeclaration(
                "platform-settings",
                "platform",
                ["api"],
                optional: false,
                Manifest("platform", "platform-settings", "ConfigurationStore")),
            remote => remote.Endpoint("api", "https://platform.example.test:7443"));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => ((IApplicationProviderBuilder)builder).UseSecretStore("platform-settings"));

        // Assert
        exception.ParamName.ShouldBe("store");
        exception.Message.ShouldContain(
            "Resource 'platform-settings' of application 'platform' is kind 'ConfigurationStore'",
            Case.Sensitive);
        builder.Providers.Sources.ShouldBeEmpty();
        builder.Providers.CommandInputs.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore by name: an unknown resource is registered for validation to check")]
    public void UseSecretStore_ByNameForUnknownResource_ShouldRegister()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);

        // Act
        ((IApplicationProviderBuilder)builder).UseSecretStore("secrets");

        // Assert
        builder.Providers.Sources["secrets"].ShouldBeOfType<SecretStoreSourceProvider>();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - UseSecretStore by name: rejects a blank name and a null surface")]
    public void UseSecretStore_ByNameWithInvalidArguments_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);

        // Act
        ArgumentException blank = Should.Throw<ArgumentException>(() => ((IApplicationProviderBuilder)builder).UseSecretStore(" "));
        ArgumentException missing = Should.Throw<ArgumentException>(() => ((IApplicationProviderBuilder)builder).UseSecretStore(default(ResourceName)));
        ArgumentNullException surface = Should.Throw<ArgumentNullException>(
            () => SecretStoreOrchestrationExtensions.UseSecretStore((IApplicationProviderBuilder)null!, (ResourceName)"secrets"));

        // Assert
        blank.ParamName.ShouldBe("store");
        missing.ParamName.ShouldBe("store");
        surface.ParamName.ShouldBe("application");
        builder.Providers.Sources.ShouldBeEmpty();
    }

    private static ApplicationDeclaration Declare(IApplicationModel model) =>
        new(model.Name, new FixedModelResolver(model));

    // The member's own gateway builds it with its UseSecretStore registration; its describe output,
    // which the set imports, carries no providers.
    private static IApplicationModel ImportMember(string application)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(application),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(new StoreGateway(new StoreController("secrets", new Dictionary<string, int>())));
        IApplicationResourceDescriptor store = builder.AddResource(Manifest(application, "secrets", "SecretStore"));
        builder.AddResource(Manifest(application, "api", "Web") with
            {
                Mounts =
                [
                    new ResourceManifestMount
                    {
                        Name = "api-key",
                        Kind = ResourceMountKind.Secret,
                        ContainerPath = "/cohesion/mounts/api-key",
                        Source = "secrets:" + secretPath,
                    },
                ],
                References =
                [
                    new ResourceManifestReference
                    {
                        Resource = "secrets",
                        Application = application,
                        Endpoints = ["api"],
                        Manifest = "Example.Store.ApplicationModel",
                    },
                ],
            })
            .DependsOn(store);
        builder.UseSecretStore(store);
        IApplicationModel imported = ApplicationModelDocument.Create(builder.Build().Model)
            .ToModel(GatewayRunMode.Apply, (ResourceName)"test");
        imported.Providers.ShouldBeSameAs(ApplicationProviders.Empty);
        return imported;
    }

    private static ResourceManifest Manifest(string application, string name, string kind) => new()
    {
        Name = name,
        Kind = kind,
        Application = application,
        ApplicationModel = "Example.Store.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Example.Store",
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "api",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080,
            },
        ],
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "api",
            Path = "/cohesion/v1",
        },
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.Deployment,
            Replicas = 1,
            MaxReplicas = 1,
        },
    };

    private sealed class FixedModelResolver : IApplicationModelResolver
    {
        private readonly IApplicationModel _model;

        public FixedModelResolver(IApplicationModel model)
        {
            _model = model;
        }

        public ValueTask<IApplicationModel> ResolveAsync(
            ApplicationModelResolutionContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_model);
    }
}
