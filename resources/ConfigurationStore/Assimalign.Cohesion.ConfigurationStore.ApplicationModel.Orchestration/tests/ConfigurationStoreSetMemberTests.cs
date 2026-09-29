using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// <c>UseConfigurationStore(name)</c> on an application-set member. A member's model is imported
/// from its describe output, which carries no providers, so the set registers them for that member
/// alone; a real gateway then resolves the member's Configuration mounts through the real
/// ConfigurationStore provider against a loopback store double.
/// </summary>
public sealed class ConfigurationStoreSetMemberTests
{
    private const string storeName = "configuration";
    private const string namespaceTarget = "/cohesion/v1/namespaces?name=api";

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - Set member: UseConfigurationStore resolves each member's configuration mounts through its own store")]
    public async Task UseConfigurationStore_OnSetMembers_ShouldResolveEachMembersConfigurationMounts()
    {
        // Arrange
        await using var appaStore = new LoopbackStore();
        await using var appbStore = new LoopbackStore();
        appaStore.Respond(namespaceTarget, "application/json", "{\"Optional\":null,\"Mode\":\"appa\"}");
        appbStore.Respond(namespaceTarget, "application/json", "{\"Mode\":\"appb\"}");
        var controller = new StoreController(
            storeName,
            new Dictionary<string, int> { ["appa"] = appaStore.Port, ["appb"] = appbStore.Port });
        var gateway = new StoreGateway(controller);
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(ImportMember("appa")), appa => appa.UseConfigurationStore(storeName))
            .AddApplication(Declare(ImportMember("appb")), appb => appb.UseConfigurationStore(storeName));

        try
        {
            // Act
            await set.RunAsync();

            // Assert
            Encoding.UTF8.GetString(controller.Inputs["appa/api"].Mounts["settings"].Content.Span)
                .ShouldBe("{\"Mode\":\"appa\",\"Optional\":null}");
            Encoding.UTF8.GetString(controller.Inputs["appb/api"].Mounts["settings"].Content.Span)
                .ShouldBe("{\"Mode\":\"appb\"}");
            LoopbackStoreRequest appaRequest = appaStore.Requests.ShouldHaveSingleItem();
            appaRequest.Method.ShouldBe("GET");
            appaRequest.Target.ShouldBe(namespaceTarget);
            string credential = appaRequest.Authorization.ShouldNotBeNull();
            credential.ShouldStartWith("Bearer ", Case.Sensitive);
            JsonWebToken token = JsonWebToken.Parse(credential["Bearer ".Length..]);
            token.Audiences.ShouldBe([storeName]);
            token.Issuer.ShouldBe("appa");
            appbStore.Requests.ShouldHaveSingleItem().Target.ShouldBe(namespaceTarget);
            controller.Models["appa"].Providers.Sources[storeName].ShouldBeOfType<ConfigurationStoreSourceProvider>();
            controller.Models["appa"].Providers.Sources[storeName]
                .ShouldNotBeSameAs(controller.Models["appb"].Providers.Sources[storeName]);
        }
        finally
        {
            await ((IMultiModelApplicationGateway)gateway).StopAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - Set member: a store-backed member without UseConfigurationStore fails naming the member and the verb")]
    public async Task RunAsync_MemberWithoutUseConfigurationStore_ShouldNameMemberAndVerb()
    {
        // Arrange
        var controller = new StoreController(storeName, new Dictionary<string, int>());
        var gateway = new StoreGateway(controller);
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(ImportMember("appa")), appa => appa.UseConfigurationStore(storeName))
            .AddApplication(Declare(ImportMember("appb")));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appb' has invalid provider registrations.", Case.Sensitive);
        exception.Message.ShouldContain("Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration", Case.Sensitive);
        exception.Message.ShouldContain(
            "set.AddApplication(Applications.<Member>, application => application.UseConfigurationStore(\"configuration\"))",
            Case.Sensitive);
        controller.Reconciled.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - Set member: UseConfigurationStore refuses a member resource of another kind, naming the member")]
    public async Task UseConfigurationStore_OnSetMemberWithOtherKind_ShouldNameMember()
    {
        // Arrange
        var controller = new StoreController(storeName, new Dictionary<string, int>());
        var gateway = new StoreGateway(controller);
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--gateway=test", "--environment=Local"])
            .AddApplication(Declare(ImportMember("appa")), appa => appa.UseConfigurationStore("api"));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => set.RunAsync());

        // Assert
        exception.Message.ShouldStartWith("Application-set member 'appa' could not register its providers:", Case.Sensitive);
        exception.Message.ShouldContain("Resource 'api' of application 'appa' is kind 'Web'", Case.Sensitive);
        exception.InnerException.ShouldBeOfType<ArgumentException>().ParamName.ShouldBe("store");
        controller.Reconciled.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore by name: registers on a registration surface and returns it")]
    public void UseConfigurationStore_ByName_ShouldRegisterAndReturnSurface()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifests.Store("settings-a"));
        IApplicationProviderBuilder surface = (IApplicationProviderBuilder)builder;

        // Act
        IApplicationProviderBuilder result = surface
            .UseConfigurationStore("settings-a")
            .UseConfigurationStore("settings-b");

        // Assert
        result.ShouldBeSameAs(builder);
        builder.Providers.Sources.Keys.ShouldBe(["settings-a", "settings-b"], ignoreOrder: true);
        builder.Providers.Sources["settings-a"].ShouldBeOfType<ConfigurationStoreSourceProvider>();
        builder.Providers.Sources["settings-b"].ShouldBeOfType<ConfigurationStoreSourceProvider>();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore by name: repeating the call keeps the first registration")]
    public void UseConfigurationStore_ByNameCalledTwice_ShouldKeepFirstRegistration()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        ((IApplicationProviderBuilder)builder).UseConfigurationStore((ResourceName)SourceRequestFactory.StoreName);
        IResourceSourceProvider first = builder.Providers.Sources[SourceRequestFactory.StoreName];

        // Act
        ((IApplicationProviderBuilder)builder).UseConfigurationStore((ResourceName)SourceRequestFactory.StoreName);

        // Assert
        builder.Providers.Sources.Count.ShouldBe(1);
        builder.Providers.Sources[SourceRequestFactory.StoreName].ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore by name: never replaces another provider")]
    public void UseConfigurationStore_ByNameWithOtherProvider_ShouldThrowAndKeepRegistration()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        var custom = new StubSourceProvider();
        builder.Providers.Sources[SourceRequestFactory.StoreName] = custom;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ((IApplicationProviderBuilder)builder).UseConfigurationStore((ResourceName)SourceRequestFactory.StoreName));

        // Assert
        exception.Message.ShouldContain($"already registered with provider '{nameof(StubSourceProvider)}'", Case.Sensitive);
        builder.Providers.Sources[SourceRequestFactory.StoreName].ShouldBeSameAs(custom);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore by name: a known resource of another kind is refused")]
    public void UseConfigurationStore_ByNameWithOtherKind_ShouldThrowAndRegisterNothing()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifests.Store("secrets", "SecretStore"));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ((IApplicationProviderBuilder)builder).UseConfigurationStore((ResourceName)"secrets"));

        // Assert
        exception.ParamName.ShouldBe("store");
        exception.Message.ShouldContain("Resource 'secrets' of application 'appa' is kind 'SecretStore'", Case.Sensitive);
        builder.Providers.Sources.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore by name: another application's resource of another kind is refused, naming its application")]
    public void UseConfigurationStore_ByNameForOtherApplicationsResourceOfOtherKind_ShouldNameResourceApplication()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.RemoteReference(
            new ExternalResourceDeclaration(
                "platform-secrets",
                "platform",
                ["api"],
                optional: false,
                TestManifests.Store("platform-secrets", "SecretStore") with { Application = "platform" }),
            remote => remote.Endpoint("api", "https://platform.example.test:7443"));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ((IApplicationProviderBuilder)builder).UseConfigurationStore((ResourceName)"platform-secrets"));

        // Assert
        exception.ParamName.ShouldBe("store");
        exception.Message.ShouldContain(
            "Resource 'platform-secrets' of application 'platform' is kind 'SecretStore'",
            Case.Sensitive);
        builder.Providers.Sources.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore by name: rejects a blank name and a null surface")]
    public void UseConfigurationStore_ByNameWithInvalidArguments_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();

        // Act
        ArgumentException blank = Should.Throw<ArgumentException>(() => ((IApplicationProviderBuilder)builder).UseConfigurationStore((ResourceName)" "));
        ArgumentException missing = Should.Throw<ArgumentException>(() => ((IApplicationProviderBuilder)builder).UseConfigurationStore(default(ResourceName)));
        ArgumentNullException surface = Should.Throw<ArgumentNullException>(
            () => ConfigurationStoreOrchestrationExtensions.UseConfigurationStore(
                (IApplicationProviderBuilder)null!,
                (ResourceName)storeName));

        // Assert
        blank.ParamName.ShouldBe("store");
        missing.ParamName.ShouldBe("store");
        surface.ParamName.ShouldBe("application");
        builder.Providers.Sources.ShouldBeEmpty();
    }

    private static IApplicationBuilder CreateBuilder() => Application
        .CreateBuilder(ApplicationName.Parse("appa"), [])
        .UseGateway(new FakeGateway());

    private static ApplicationDeclaration Declare(IApplicationModel model) =>
        new(model.Name, new FixedModelResolver(model));

    // The member's own gateway builds it with its UseConfigurationStore registration; its describe
    // output, which the set imports, carries no providers.
    private static IApplicationModel ImportMember(string application)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse(application),
                ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(new StoreGateway(new StoreController(storeName, new Dictionary<string, int>())));
        IApplicationResourceDescriptor store = builder.AddResource(TestManifests.Store(storeName) with
        {
            Application = application,
        });
        builder.AddResource(TestManifests.Consumer("api", storeName + ":api") with
            {
                Kind = "Web",
                Application = application,
                References =
                [
                    new ResourceManifestReference
                    {
                        Resource = storeName,
                        Application = application,
                        Endpoints = ["api"],
                        Manifest = "Example.AppA.ApplicationModel",
                    },
                ],
            })
            .DependsOn(store);
        builder.UseConfigurationStore(store);
        IApplicationModel imported = ApplicationModelDocument.Create(builder.Build().Model)
            .ToModel(GatewayRunMode.Apply, (ResourceName)"test");
        imported.Providers.ShouldBeSameAs(ApplicationProviders.Empty);
        return imported;
    }

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
